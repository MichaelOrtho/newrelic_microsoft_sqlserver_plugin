using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Text;

using log4net;

using NewRelic.Microsoft.SqlServer.Plugin.Configuration;
using NewRelic.Microsoft.SqlServer.Plugin.Core.Extensions;
using NewRelic.Microsoft.SqlServer.Plugin.Properties;
using NewRelic.Microsoft.SqlServer.Plugin.QueryTypes;

namespace NewRelic.Microsoft.SqlServer.Plugin
{
    public abstract class SqlEndpointBase : ISqlEndpoint
    {
        private static readonly ILog _ErrorDetailOutputLogger = LogManager.GetLogger(Constants.ErrorDetailLogger);
        private static readonly ILog _VerboseSqlOutputLogger = LogManager.GetLogger(Constants.VerboseSqlLogger);

        private SqlQuery[] _queries;

        protected SqlEndpointBase(string name, string connectionString)
        {
            Name = name;
            ConnectionString = connectionString;

            SqlDmlActivityHistory = new Dictionary<string, SqlDmlActivity>();

            IncludedDatabases = new Database[0];
            ExcludedDatabaseNames = new string[0];
        }

        protected abstract string ComponentGuid { get; }

        protected Dictionary<string, SqlDmlActivity> SqlDmlActivityHistory { get; set; }

        public Database[] IncludedDatabases { get; protected set; }

        public string[] IncludedDatabaseNames
        {
            get { return IncludedDatabases.Select(d => d.Name).ToArray(); }
        }

        public string[] ExcludedDatabaseNames { get; protected set; }

        public string Name { get; private set; }
        public string ConnectionString { get; private set; }

        public void SetQueries(IEnumerable<SqlQuery> queries)
        {
            _queries = FilterQueries(queries).ToArray();
        }

        public virtual IEnumerable<IQueryContext> ExecuteQueries(ILog log)
        {
            return ExecuteQueries(_queries, ConnectionString, log);
        }

        public virtual void ToLog(ILog log)
        {
            // Remove password from logging
            var safeConnectionString = new SqlConnectionStringBuilder(ConnectionString);
            if (!string.IsNullOrEmpty(safeConnectionString.Password))
            {
                safeConnectionString.Password = "[redacted]";
            }

            log.InfoFormat("      {0}: {1}", Name, safeConnectionString);

            // Validate that connection string do not provide both Trusted Security AND user/password
            bool hasUserCreds = !string.IsNullOrEmpty(safeConnectionString.UserID) || !string.IsNullOrEmpty(safeConnectionString.Password);
            if (safeConnectionString.IntegratedSecurity == hasUserCreds)
            {
                log.Error("==================================================");
                log.ErrorFormat("Connection string for '{0}' may not contain both Integrated Security and User ID/Password credentials. " +
                                "Review the readme.md and update the config file.",
                    safeConnectionString.DataSource);
                log.Error("==================================================");
            }
        }

        protected IEnumerable<IQueryContext> ExecuteQueries(SqlQuery[] queries, string connectionString, ILog log)
        {
            // Remove password from logging
            var safeConnectionString = new SqlConnectionStringBuilder(connectionString);
            if (!string.IsNullOrEmpty(safeConnectionString.Password))
            {
                safeConnectionString.Password = "[redacted]";
            }

            _VerboseSqlOutputLogger.InfoFormat("Connecting with {0}", safeConnectionString);

            using (var conn = new SqlConnection(connectionString))
            {
                foreach (SqlQuery query in queries)
                {
                    object[] results;
                    try
                    {
                        // Raw results from the database
                        results = query.Query(conn, this).ToArray();
                        // Log them
                        LogVerboseSqlResults(query, results);
                        // Allow them to be transformed
                        results = OnQueryExecuted(query, results, log);
                    }
                    catch (Exception e)
                    {
                        _ErrorDetailOutputLogger.Error(string.Format("Error with query '{0}' at endpoint '{1}'", query.QueryName, safeConnectionString), e);
                        LogErrorSummary(log, e, query);
                        continue;
                    }
                    yield return CreateQueryContext(query, results);
                }
            }
        }

        protected static void LogVerboseSqlResults(ISqlQuery query, IEnumerable<object> results)
        {
            // This could be slow, so only proceed if it actually gets logged
            if (!_VerboseSqlOutputLogger.IsInfoEnabled) return;

            var verboseLogging = new StringBuilder();
            verboseLogging.AppendFormat("Executed {0}", query.ResourceName).AppendLine();

            foreach (object result in results)
            {
                verboseLogging.AppendLine(result.ToString());
            }

            _VerboseSqlOutputLogger.Info(verboseLogging.ToString());
        }

        internal QueryContext CreateQueryContext(IMetricQuery query, IEnumerable<object> results)
        {
            return new QueryContext(query) {Results = results, ComponentName = Name, ComponentGuid = ComponentGuid };
        }

        protected internal abstract IEnumerable<SqlQuery> FilterQueries(IEnumerable<SqlQuery> queries);

        protected virtual object[] OnQueryExecuted(ISqlQuery query, object[] results, ILog log)
        {
            // TODO: We should be able to remove the special casing of SqlDmlActivity here, but simply changing it to a delta counter doe
            return query.QueryType == typeof (SqlDmlActivity) ? CalculateSqlDmlActivityIncrease(results, log) : results;
        }

        public override string ToString()
        {
            return string.Format("Name: {0}, ConnectionString: {1}", Name, ConnectionString);
        }

        internal object[] CalculateSqlDmlActivityIncrease(object[] inputResults, ILog log)
        {
            if (inputResults == null || inputResults.Length == 0)
            {
                log.Error("No values passed to CalculateSqlDmlActivityIncrease");
                return inputResults;
            }

            SqlDmlActivity[] sqlDmlActivities = inputResults.OfType<SqlDmlActivity>().ToArray();

            if (!sqlDmlActivities.Any())
            {
                log.Error("In trying to Process results for SqlDmlActivity, results were NULL or not of the appropriate type");
                return inputResults;
            }

            Dictionary<string, SqlDmlActivity> currentValues = sqlDmlActivities
                .GroupBy(a => string.Format("{0}:{1}:{2}:{3}", BitConverter.ToString(a.PlanHandle), BitConverter.ToString(a.SqlStatementHash), a.CreationTime.Ticks, a.QueryType))
                .Select(a => new
                             {
                                 a.Key,
                                 //If we ever gets dupes, sum Excution Count
                                 Activity = new SqlDmlActivity
                                            {
                                                CreationTime = a.First().CreationTime,
                                                SqlStatementHash = a.First().SqlStatementHash,
                                                PlanHandle = a.First().PlanHandle,
                                                QueryType = a.First().QueryType,
                                                ExecutionCount = a.Sum(dml => dml.ExecutionCount),
                                            }
                             })
                .ToDictionary(a => a.Key, a => a.Activity);

            long reads = 0;
            long writes = 0;

            // If this is the first time through, reads and writes are definitely 0
            if (SqlDmlActivityHistory.Count > 0)
            {
                currentValues
                    .ForEach(a =>
                             {
                                 long increase;

                                 // Find a matching previous value for a delta
                                 SqlDmlActivity previous;
                                 if (!SqlDmlActivityHistory.TryGetValue(a.Key, out previous))
                                 {
                                     // Nothing previous, the delta is the absolute value here
                                     increase = a.Value.ExecutionCount;
                                 }
                                 else if (a.Value.QueryType == previous.QueryType)
                                 {
                                     // Calculate the delta
                                     increase = a.Value.ExecutionCount - previous.ExecutionCount;

                                     // Only record positive deltas, though theoretically impossible here
                                     if (increase <= 0) return;
                                 }
                                 else
                                 {
                                     return;
                                 }

                                 switch (a.Value.QueryType)
                                 {
                                     case "Writes":
                                         writes += increase;
                                         break;
                                     case "Reads":
                                         reads += increase;
                                         break;
                                 }
                             });
            }

            //Current Becomes the new history
            SqlDmlActivityHistory = currentValues;

            if (_VerboseSqlOutputLogger.IsInfoEnabled)
            {
                _VerboseSqlOutputLogger.InfoFormat("SQL DML Activity: Reads={0} Writes={1}", reads, writes);
                _VerboseSqlOutputLogger.Info("");
            }

            //return the sum of all increases for reads and writes
            //if there is was no history (first time for this db) then reads and writes will be 0
            return new object[]
                   {
                       new SqlDmlActivity
                       {
                           Reads = reads,
                           Writes = writes,
                       },
                   };
        }

        private void LogErrorSummary(ILog log, Exception e, ISqlQuery query)
        {
            var sqlException = e.InnerException as SqlException;
            if (sqlException == null) return;

            log.LogSqlException(sqlException, query, ConnectionString);
        }
    }
}
