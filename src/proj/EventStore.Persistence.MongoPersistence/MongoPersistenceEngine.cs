﻿namespace EventStore.Persistence.MongoPersistence
{
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Threading;
	using Logging;
	using MongoDB.Bson;
	using MongoDB.Driver;
	using MongoDB.Driver.Builders;
	using Serialization;

	public class MongoPersistenceEngine : IPersistStreams
	{
		private const string ConcurrencyException = "E1100";
		private static readonly ILog Logger = LogFactory.BuildLogger(typeof(MongoPersistenceEngine));
		private readonly MongoCollectionSettings<BsonDocument> commitSettings;
		private readonly MongoCollectionSettings<BsonDocument> snapshotSettings;
		private readonly MongoCollectionSettings<BsonDocument> streamSettings;
		private readonly MongoDatabase store;
		private readonly IDocumentSerializer serializer;
		private bool disposed;
		private int initialized;

		public MongoPersistenceEngine(MongoDatabase store, IDocumentSerializer serializer)
		{
			if (store == null)
				throw new ArgumentNullException("store");

			if (serializer == null)
				throw new ArgumentNullException("serializer");

			this.store = store;
			this.serializer = serializer;

			this.commitSettings = this.store.CreateCollectionSettings<BsonDocument>("Commits");
			this.commitSettings.AssignIdOnInsert = false;
			this.commitSettings.SafeMode = SafeMode.True;

			this.snapshotSettings = this.store.CreateCollectionSettings<BsonDocument>("Snapshots");
			this.snapshotSettings.AssignIdOnInsert = false;
			this.snapshotSettings.SafeMode = SafeMode.False;

			this.streamSettings = this.store.CreateCollectionSettings<BsonDocument>("Streams");
			this.streamSettings.AssignIdOnInsert = false;
			this.streamSettings.SafeMode = SafeMode.False;
		}

		public void Dispose()
		{
			this.Dispose(true);
			GC.SuppressFinalize(this);
		}
		protected virtual void Dispose(bool disposing)
		{
			if (!disposing || this.disposed)
				return;

			Logger.Debug(Messages.ShuttingDownPersistence);
			this.disposed = true;
		}

		public virtual void Initialize()
		{
			if (Interlocked.Increment(ref this.initialized) > 1)
				return;

			Logger.Debug(Messages.InitializingStorage);

			this.TryMongo(() =>
			{
				this.PersistedCommits.EnsureIndex(
					IndexKeys.Ascending("Dispatched").Ascending("CommitStamp"),
					IndexOptions.SetName("Dispatched_Index").SetUnique(false));

				this.PersistedCommits.EnsureIndex(
					IndexKeys.Ascending("_id.StreamId", "Events.StreamRevision"),
					IndexOptions.SetName("GetFrom_Index").SetUnique(true));

				this.PersistedCommits.EnsureIndex(
					IndexKeys.Ascending("CommitStamp"),
					IndexOptions.SetName("CommitStamp_Index").SetUnique(false));

				this.PersistedStreamHeads.EnsureIndex(
					IndexKeys.Ascending("Unsnapshotted"),
					IndexOptions.SetName("Unsnapshotted_Index").SetUnique(false));
			});
		}

		public virtual IEnumerable<Commit> GetFrom(string streamId, int minRevision, int maxRevision)
		{
			Logger.Debug(Messages.GettingAllCommitsBetween, streamId, minRevision, maxRevision);

			return this.TryMongo(() =>
			{
				var query = Query.And(
					Query.EQ("_id.StreamId", streamId),
					Query.GTE("Events.StreamRevision", minRevision),
					Query.LTE("Events.StreamRevision", maxRevision));

				return this.PersistedCommits
					.Find(query)
					.SetSortOrder("Events.StreamRevision")
					.Select(mc => mc.ToCommit(this.serializer));
			});
		}
		public virtual IEnumerable<Commit> GetFrom(DateTime start)
		{
			Logger.Debug(Messages.GettingAllCommitsFrom, start);

			return this.TryMongo(() => this.PersistedCommits
				.Find(Query.GTE("CommitStamp", start))
				.SetSortOrder("CommitStamp")
				.Select(x => x.ToCommit(this.serializer)));
		}

		public virtual IEnumerable<Commit> GetFromTo(DateTime start, DateTime end)
		{
			Logger.Debug(Messages.GettingAllCommitsFromTo, start, end);

			return this.TryMongo(() => this.PersistedCommits
				.Find(Query.And(Query.GTE("CommitStamp", start), Query.LT("CommitStamp", end)))
				.SetSortOrder("CommitStamp")
				.Select(x => x.ToCommit(this.serializer)));
		}

		public virtual void Commit(Commit attempt)
		{
			Logger.Debug(Messages.AttemptingToCommit,
				attempt.Events.Count, attempt.StreamId, attempt.CommitSequence);

			this.TryMongo(() =>
			{
				var commit = attempt.ToMongoCommit(this.serializer);

				try
				{
					// for concurrency / duplicate commit detection safe mode is required
					this.PersistedCommits.Insert(commit, SafeMode.True);
					this.UpdateStreamHeadAsync(attempt.StreamId, attempt.StreamRevision, attempt.Events.Count);
					Logger.Debug(Messages.CommitPersisted, attempt.CommitId);
				}
				catch (MongoException e)
				{
					if (!e.Message.Contains(ConcurrencyException))
						throw;

					var savedCommit = this.PersistedCommits.FindOne(attempt.ToMongoCommitIdQuery()).ToCommit(this.serializer);
					if (savedCommit.CommitId == attempt.CommitId)
						throw new DuplicateCommitException();

					Logger.Debug(Messages.ConcurrentWriteDetected);
					throw new ConcurrencyException();
				}
			});
		}

		public virtual IEnumerable<Commit> GetUndispatchedCommits()
		{
			Logger.Debug(Messages.GettingUndispatchedCommits);

			return this.TryMongo(() => this.PersistedCommits
				.Find(Query.EQ("Dispatched", false))
				.SetSortOrder("CommitStamp")
				.Select(mc => mc.ToCommit(this.serializer)));
		}
		public virtual void MarkCommitAsDispatched(Commit commit)
		{
			Logger.Debug(Messages.MarkingCommitAsDispatched, commit.CommitId);

			this.TryMongo(() =>
			{
				var query = commit.ToMongoCommitIdQuery();
				var update = Update.Set("Dispatched", true);
				this.PersistedCommits.Update(query, update);
			});
		}

		public virtual IEnumerable<StreamHead> GetStreamsToSnapshot(int maxThreshold)
		{
			Logger.Debug(Messages.GettingStreamsToSnapshot);

			return this.TryMongo(() =>
			{
				var query = Query.GTE("Unsnapshotted", maxThreshold);

				return this.PersistedStreamHeads
					.Find(query)
					.SetSortOrder(SortBy.Descending("Unsnapshotted"))
					.Select(x => x.ToStreamHead());
			});
		}
        public virtual Snapshot GetSnapshot(string streamId, int maxRevision)
		{
			Logger.Debug(Messages.GettingRevision, streamId, maxRevision);

			return this.TryMongo(() => this.PersistedSnapshots
				.Find(streamId.ToSnapshotQuery(maxRevision))
				.SetSortOrder(SortBy.Descending("_id"))
				.SetLimit(1)
				.Select(mc => mc.ToSnapshot(this.serializer))
				.FirstOrDefault());
		}
		public virtual bool AddSnapshot(Snapshot snapshot)
		{
			if (snapshot == null)
				return false;

			Logger.Debug(Messages.AddingSnapshot, snapshot.StreamId, snapshot.StreamRevision);

			try
			{
				var mongoSnapshot = snapshot.ToMongoSnapshot(this.serializer);
				var query = Query.EQ("_id", mongoSnapshot["_id"]);
				var update = Update.Set("Payload", mongoSnapshot["Payload"]);

				// Doing an upsert instead of an insert allows us to overwrite an existing snapshot and not get stuck with a
				// stream that needs to be snapshotted because the insert fails and the SnapshotRevision isn't being updated.
				this.PersistedSnapshots.Update(query, update, UpdateFlags.Upsert);

				// More commits could have been made between us deciding that a snapshot is required and writing it so just
				// resetting the Unsnapshotted count may be a little off. Adding snapshots should be a separate process so
				// this is a good chance to make sure the numbers are still in-sync - it only adds a 'read' after all ...
				var streamHead = this.PersistedStreamHeads.FindOneById(snapshot.StreamId).ToStreamHead();
				var unsnapshotted = streamHead.HeadRevision - snapshot.StreamRevision;
				this.PersistedStreamHeads.Update(
					Query.EQ("_id", snapshot.StreamId),
					Update.Set("SnapshotRevision", snapshot.StreamRevision).Set("Unsnapshotted", unsnapshotted));

				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}

		public virtual void Purge()
		{
			Logger.Warn(Messages.PurgingStorage);

			this.PersistedCommits.Drop();
			this.PersistedStreamHeads.Drop();
			this.PersistedSnapshots.Drop();
		}

        private void UpdateStreamHeadAsync(string streamId, int streamRevision, int eventsCount)
		{
			ThreadPool.QueueUserWorkItem(x => this.TryMongo(() =>
			{
				this.PersistedStreamHeads.Update(
					Query.EQ("_id", streamId),
					Update.Set("HeadRevision", streamRevision).Inc("SnapshotRevision", 0).Inc("Unsnapshotted", eventsCount),
					UpdateFlags.Upsert);
			}), null);
		}

		protected virtual MongoCollection<BsonDocument> PersistedCommits
		{
			get { return this.store.GetCollection(this.commitSettings); }
		}
		protected virtual MongoCollection<BsonDocument> PersistedStreamHeads
		{
			get { return this.store.GetCollection(this.streamSettings); }
		}
		protected virtual MongoCollection<BsonDocument> PersistedSnapshots
		{
			get { return this.store.GetCollection(this.snapshotSettings); }
		}

		protected virtual T TryMongo<T>(Func<T> callback)
		{
			var results = default(T);

			this.TryMongo(() =>
			{
				results = callback();
			});

			return results;
		}
		protected virtual void TryMongo(Action callback)
		{
			if (this.disposed)
				throw new ObjectDisposedException("Attempt to use storage after it has been disposed.");

			try
			{
				callback();
			}
			catch (MongoConnectionException e)
			{
				Logger.Warn(Messages.StorageUnavailable);
				throw new StorageUnavailableException(e.Message, e);
			}
			catch (MongoException e)
			{
				Logger.Error(Messages.StorageThrewException, e.GetType());
				throw new StorageException(e.Message, e);
			}
		}
	}
}