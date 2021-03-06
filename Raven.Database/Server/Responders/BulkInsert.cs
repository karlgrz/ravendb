﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading;
using NetTopologySuite.IO;
using Raven.Abstractions.Data;
using Raven.Abstractions.Json;
using Raven.Abstractions.Logging;
using Raven.Database.Server.Abstractions;
using Raven.Database.Tasks;
using Raven.Database.Util.Streams;
using Raven.Imports.Newtonsoft.Json;
using Raven.Imports.Newtonsoft.Json.Bson;
using Raven.Database.Extensions;
using Raven.Json.Linq;
using Task = System.Threading.Tasks.Task;

namespace Raven.Database.Server.Responders
{
	public class BulkInsert : AbstractRequestResponder
	{
		public override string UrlPattern
		{
			get { return "^/bulkInsert$"; }
		}
		public override string[] SupportedVerbs
		{
			get { return new[] { "POST" }; }
		}
		public override void Respond(IHttpContext context)
		{
			if (string.IsNullOrEmpty(context.Request.QueryString["no-op"]) == false)
			{
				// this is a no-op request which is there just to force the client HTTP layer
				// to handle the authentication
				return; 
			}

			var options = new BulkInsertOptions
			{
				CheckForUpdates = context.GetCheckForUpdates(),
				CheckReferencesInIndexes = context.GetCheckReferencesInIndexes()
			};

			var sp = Stopwatch.StartNew();

			var status = new RavenJObject
			{
				{"Documents", 0},
				{"Completed", false}
			};

			int documents = 0;
			var mre = new ManualResetEventSlim(false);

			var currentDatbase = Database;
			var task = Task.Factory.StartNew(() =>
			{
				documents = currentDatbase.BulkInsert(options, YieldBatches(context, mre));
				status["Documents"] = documents;
				status["Completed"] = true;
			});

			long id;
			Database.AddTask(task, status, out id);

			mre.Wait(Database.WorkContext.CancellationToken);

			context.Log(log => log.Debug("\tBulk inserted received {0:#,#;;0} documents in {1}, task #: {2}", documents, sp.Elapsed, id));

			context.WriteJson(new
			{
				OperationId = id
			});
		}

		private static IEnumerable<IEnumerable<JsonDocument>> YieldBatches(IHttpContext context, ManualResetEventSlim mre)
		{
			try
			{
				using (var inputStream = context.Request.GetBufferLessInputStream())
				{
					var binaryReader = new BinaryReader(inputStream);
					while (true)
					{
						int size;
						try
						{
							size = binaryReader.ReadInt32();
						}
						catch (EndOfStreamException)
						{
							break;
						}
						using (var stream = new PartialStream(inputStream, size))
						{
							yield return YieldDocumentsInBatch(stream);
						}
					}
				}
			}
			finally
			{
				mre.Set();
			}
		}

		private static IEnumerable<JsonDocument> YieldDocumentsInBatch(Stream partialStream)
		{
			using(var stream = new GZipStream(partialStream, CompressionMode.Decompress, leaveOpen:true))
			{
				var reader = new BinaryReader(stream);
				var count = reader.ReadInt32();
				for (int i = 0; i < count; i++)
				{
					var doc = (RavenJObject)RavenJToken.ReadFrom(new BsonReader(reader));

					var metadata = doc.Value<RavenJObject>("@metadata");

					if (metadata == null)
						throw new InvalidOperationException("Could not find metadata for document");

					var id = metadata.Value<string>("@id");
					if (string.IsNullOrEmpty(id))
						throw new InvalidOperationException("Could not get id from metadata");

					doc.Remove("@metadata");

					yield return new JsonDocument
					{
						Key = id,
						DataAsJson = doc,
						Metadata = metadata
					};
				}
			}
		}
	}
}