﻿using System;
using System.Linq;
using System.Threading;
using System.Collections.Generic;

using Microsoft.Azure.Documents.Linq;
using Microsoft.Azure.Documents.Client;

using Hangfire.Server;
using Hangfire.Logging;
using Hangfire.AzureDocumentDB.Entities;

namespace Hangfire.AzureDocumentDB
{
#pragma warning disable 618
    internal class ExpirationManager : IServerComponent
#pragma warning restore 618
    {
        private static readonly ILog Logger = LogProvider.For<ExpirationManager>();
        private const string distributedLockKey = "expirationmanager";
        private static readonly TimeSpan defaultLockTimeout = TimeSpan.FromMinutes(5);
        private static readonly string[] documents = { "locks", "jobs", "lists", "sets", "hashes", "counters" };
        private readonly TimeSpan checkInterval;

        private readonly AzureDocumentDbStorage storage;

        public ExpirationManager(AzureDocumentDbStorage storage)
        {
            if (storage == null) throw new ArgumentNullException(nameof(storage));

            this.storage = storage;
            checkInterval = storage.Options.ExpirationCheckInterval;
        }

        public void Execute(CancellationToken cancellationToken)
        {
            foreach (string document in documents)
            {
                Logger.Debug($"Removing outdated records from the '{document}' document.");
                DocumentTypes type = document.ToDocumentType();

                using (new AzureDocumentDbDistributedLock(distributedLockKey, defaultLockTimeout, storage))
                {
                    Uri collectionUri = document.ToDocumentCollectionUri(storage);
                    FeedOptions QueryOptions = new FeedOptions { MaxItemCount = 50, };
                    IDocumentQuery<DocumentEntity> query = storage.Client.CreateDocumentQuery<DocumentEntity>(collectionUri, QueryOptions)
                        .Where(d => d.DocumentType == type)
                        .AsDocumentQuery();

                    while (query.HasMoreResults)
                    {
                        FeedResponse<DocumentEntity> response = query.ExecuteNextAsync<DocumentEntity>(cancellationToken).GetAwaiter().GetResult();

                        List<DocumentEntity> entities = response
                            .Where(c => c.ExpireOn < DateTime.UtcNow)
                            .Where(entity => document != "counters" || !(entity is Counter) || ((Counter)entity).Type != CounterTypes.Raw).ToList();

                        foreach (DocumentEntity entity in entities)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            storage.Client.DeleteDocumentAsync(entity.SelfLink).GetAwaiter().GetResult();
                        }
                    }
                }

                Logger.Trace($"Outdated records removed from the '{document}' document.");
                cancellationToken.WaitHandle.WaitOne(checkInterval);
            }
        }
    }

    internal static class ExpirationManagerExtenison
    {
        internal static DocumentTypes ToDocumentType(this string document)
        {
            switch (document)
            {
                case "locks": return DocumentTypes.Lock;
                case "jobs": return DocumentTypes.Job;
                case "lists": return DocumentTypes.List;
                case "sets": return DocumentTypes.Set;
                case "hashes": return DocumentTypes.Hash;
                case "counters": return DocumentTypes.Counter;
            }

            throw new ArgumentException("invalid document type", nameof(document));
        }

        internal static Uri ToDocumentCollectionUri(this string document, AzureDocumentDbStorage storage)
        {
            switch (document)
            {
                case "locks": return storage.Collections.LockDocumentCollectionUri;
                case "jobs": return storage.Collections.JobDocumentCollectionUri;
                case "lists": return storage.Collections.ListDocumentCollectionUri;
                case "sets": return storage.Collections.SetDocumentCollectionUri;
                case "hashes": return storage.Collections.HashDocumentCollectionUri;
                case "counters": return storage.Collections.CounterDocumentCollectionUri;
            }

            throw new ArgumentException("invalid document type", nameof(document));

        }
    }
}