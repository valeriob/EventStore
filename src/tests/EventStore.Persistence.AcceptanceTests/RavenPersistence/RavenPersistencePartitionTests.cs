using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using EventStore.Persistence.AcceptanceTests.Engines;
using Machine.Specifications;

#pragma warning disable 169
// ReSharper disable InconsistentNaming

namespace EventStore.Persistence.AcceptanceTests.RavenPersistence
{
    [Subject("RavenPersistence - Partitions")]
	public class when_committing_a_stream_with_the_same_id_as_a_stream_in_another_partition : using_raven_persistence_with_partitions
    {
        static IPersistStreams persistence1, persistence2;
        static Commit attempt1, attempt2;

		static Exception thrown;

        Establish context = () =>
        {
            persistence1 = NewEventStoreWithPartition();
            persistence2 = NewEventStoreWithPartition();

            var now = SystemTime.UtcNow;
            attempt1 = streamId.BuildAttempt(now);
            attempt2 = streamId.BuildAttempt(now.Subtract(TimeSpan.FromDays(1)));

            persistence1.Commit(attempt1);
        };

		Because of = () =>
			thrown = Catch.Exception(() => persistence2.Commit(attempt2));

		It should_succeed = () =>
			thrown.ShouldBeNull();

        It should_persist_to_the_correct_partition = () =>
        {
            var stream = persistence2.GetFrom(streamId, 0, int.MaxValue).ToArray();
            stream.ShouldNotBeNull();
            stream.Count().ShouldEqual(1);
            stream.First().CommitStamp.ShouldEqual(attempt2.CommitStamp);
        };

        It should_not_affect_the_stream_from_the_other_partition = () =>
        {
            var stream = persistence1.GetFrom(streamId, 0, int.MaxValue).ToArray();
            stream.ShouldNotBeNull();
            stream.Count().ShouldEqual(1);
            stream.First().CommitStamp.ShouldEqual(attempt1.CommitStamp);
        };
    }

    [Subject("RavenPersistence - Partitions")]
	public class when_saving_a_snapshot_in_a_partition : using_raven_persistence_with_partitions
    {
		static Snapshot snapshot;
        static IPersistStreams persistence1, persistence2;
		static bool added;

        Establish context = () =>
        {
            snapshot =  new Snapshot(streamId, 1, "Snapshot");
            persistence1 = NewEventStoreWithPartition();
            persistence2 = NewEventStoreWithPartition();
            persistence1.Commit(streamId.BuildAttempt());
        };
		Because of = () =>
			added = persistence1.AddSnapshot(snapshot);

		It should_indicate_the_snapshot_was_added = () =>
			added.ShouldBeTrue();

		It should_be_able_to_retrieve_the_snapshot = () =>
			persistence1.GetSnapshot(streamId, snapshot.StreamRevision).ShouldNotBeNull();

        It should_not_be_able_to_retrieve_the_snapshot_from_another_partition = () =>
            persistence2.GetSnapshot(streamId, snapshot.StreamRevision).ShouldBeNull();
    }
    
    [Subject("RavenPersistence - Partitions")]
	public class when_reading_all_commits_from_a_particular_point_in_time_from_a_partition : using_raven_persistence_with_partitions
    {
        static DateTime now;
        static IPersistStreams persistence1, persistence2;
        static Commit first, second, third, fourth, fifth;
        static Commit[] committed1, committed2;

		Establish context = () =>
		{
            now = SystemTime.UtcNow.AddYears(1);
            first = (Guid.NewGuid() + "").BuildAttempt(now.AddSeconds(1));
		    second = first.BuildNextAttempt();
		    third = second.BuildNextAttempt();
		    fourth = third.BuildNextAttempt();
            fifth = (Guid.NewGuid() + "").BuildAttempt(now.AddSeconds(1));

		    persistence1 = NewEventStoreWithPartition();
            persistence2 = NewEventStoreWithPartition();

			persistence1.Commit(first);
			persistence1.Commit(second);
			persistence1.Commit(third);
			persistence1.Commit(fourth);
            persistence2.Commit(fifth);
		};

		Because of = () =>
			committed1 = persistence1.GetFrom(now).ToArray();

		It should_return_all_commits_on_or_after_the_point_in_time_specified = () =>
			committed1.Length.ShouldEqual(4);

        It should_not_return_commits_from_other_partitions = () =>
            committed1.Any(c => c.CommitId.Equals(fifth.CommitId)).ShouldBeFalse();
    }

    [Subject("RavenPersistence - Partitions")]
	public class when_purging_all_commits : using_raven_persistence_with_partitions
	{
        static IPersistStreams persistence1, persistence2;

        Establish context = () =>
        {
            persistence1 = NewEventStoreWithPartition();
            persistence2 = NewEventStoreWithPartition();

            persistence1.Commit(streamId.BuildAttempt());
            persistence2.Commit(streamId.BuildAttempt());
        };
		Because of = () =>
		{
			Thread.Sleep(50); // 50 ms = enough time for Raven to become consistent
			persistence1.Purge();
		};

		It should_purge_all_commits_stored = () =>
			persistence1.GetFrom(DateTime.MinValue).Count().ShouldEqual(0);

        It should_purge_all_streams_to_snapshot = () =>
			persistence1.GetStreamsToSnapshot(0).Count().ShouldEqual(0);

        It should_purge_all_undispatched_commits = () =>
			persistence1.GetUndispatchedCommits().Count().ShouldEqual(0);

        It should_not_purge_all_commits_stored_in_other_partitions = () =>
            persistence2.GetFrom(DateTime.MinValue).Count().ShouldNotEqual(0);

        It should_not_purge_all_streams_to_snapshot_in_other_partitions = () =>
            persistence2.GetStreamsToSnapshot(0).Count().ShouldNotEqual(0);

        It should_not_purge_all_undispatched_commits_in_other_partitions = () =>
            persistence2.GetUndispatchedCommits().Count().ShouldNotEqual(0);
	}
    
	public abstract class using_raven_persistence_with_partitions
	{
	    protected static string streamId;
		protected static List<IPersistStreams> instantiatedPersistence;

		Establish context = () =>
		{
            streamId = Guid.NewGuid() + "";
            instantiatedPersistence = new List<IPersistStreams>();
		};

		Cleanup everything = () =>
		{
		    foreach (var persistence in instantiatedPersistence)
		    {
                persistence.Dispose();
		    }
		};
        
        protected static IPersistStreams NewEventStoreWithPartition()
        {
            return NewEventStoreWithPartition(Guid.NewGuid().ToString());
        }

        protected static IPersistStreams NewEventStoreWithPartition(string partition)
        {
            var config = AcceptanceTestRavenPersistenceFactory.GetDefaultConfig();
            config.Partition = partition;

            var persistence = new AcceptanceTestRavenPersistenceFactory(config).Build();
            persistence.Initialize();

            instantiatedPersistence.Add(persistence);

            return persistence;
        }
	}
}

// ReSharper enable InconsistentNaming
#pragma warning restore 169