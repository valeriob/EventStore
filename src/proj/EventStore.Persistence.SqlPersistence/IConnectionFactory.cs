namespace EventStore.Persistence.SqlPersistence
{
	using System;
	using System.Data;

	public interface IConnectionFactory
	{
		IDbConnection OpenMaster(string streamId);
        IDbConnection OpenReplica(string streamId);
	}
}