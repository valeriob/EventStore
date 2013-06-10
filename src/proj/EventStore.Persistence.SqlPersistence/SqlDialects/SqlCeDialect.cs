using System.Reflection;

namespace EventStore.Persistence.SqlPersistence.SqlDialects
{
	using System;
	using System.Data;
	using System.Transactions;

	public class SqlCeDialect : CommonSqlDialect
	{
		public override string InitializeStorage
		{
			get { return SqlCeStatements.InitializeStorage; }
		}
		public override string GetSnapshot
		{
			get { return base.GetSnapshot.Replace("SELECT *", "SELECT TOP(1) *").Replace("LIMIT 1", string.Empty); }
		}

		public override string GetCommitsFromStartingRevision
		{
			get { return RemovePaging(base.GetCommitsFromStartingRevision); }
		}
		private static string RemovePaging(string query)
		{
			return query
				.Replace("\n LIMIT @Limit OFFSET @Skip;", ";")
				.Replace("\n LIMIT @Limit;", ";");
		}

		public override bool CanPage
		{
			get { return false; }
		}

		public override bool IsDuplicate(Exception exception)
		{
			// using reflection to avoid a direct dependency on SQL CE assemblies
			var message = exception.Message.ToUpperInvariant();
			return message.Contains("DUPLICATE") || message.Contains("UNIQUE");
		}

		public override IDbStatement BuildStatement(
			TransactionScope scope, IDbConnection connection, IDbTransaction transaction)
		{
			return new SqlCeDbStatement(this, scope, connection, transaction);
		}

		private class SqlCeDbStatement : DelimitedDbStatement
		{
			public SqlCeDbStatement(
				ISqlDialect dialect,
				TransactionScope scope,
				IDbConnection connection,
				IDbTransaction transaction)
				: base(dialect, scope, connection, transaction)
			{
			}
            
            protected override void SetParameterValue(IDataParameter param, object value, DbType? type)
            {
                base.SetParameterValue(param, value, null);

                // SQL CE Binary has a size limit of 8000. We have to explicitely set the type of the parameter to SqlDbType.Image 
                // to be able to support a larger payload.
                if (param.DbType == DbType.Binary)
                {
                    param.GetType().InvokeMember("SqlDbType",
                                                 BindingFlags.Instance | BindingFlags.Public | BindingFlags.SetProperty,
                                                 Type.DefaultBinder, param, new object[] { SqlDbType.Image });
                }
            }
		}
	}
}