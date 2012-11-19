namespace EventStore.Persistence.RavenPersistence
{
	using System;

	public class RavenStreamHead
	{
		public string Id { get; set; }
        public string Partition { get; set; }
        public string StreamId { get; set; }
		public int HeadRevision { get; set; }
		public int SnapshotRevision { get; set; }
		public int SnapshotAge
		{
			get { return this.HeadRevision - this.SnapshotRevision; } // set by map/reduce on the server
		}
	}
}