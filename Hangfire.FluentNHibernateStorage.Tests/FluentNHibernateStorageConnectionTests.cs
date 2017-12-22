﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Hangfire.Common;
using Hangfire.FluentNHibernateStorage.Entities;
using Hangfire.FluentNHibernateStorage.JobQueue;
using Hangfire.Server;
using Hangfire.Storage;
using Moq;
using Xunit;

namespace Hangfire.FluentNHibernateStorage.Tests
{
    public class FluentNHibernateStorageConnectionTests : IClassFixture<TestDatabaseFixture>
    {
        public FluentNHibernateStorageConnectionTests()
        {
            _queue = new Mock<IPersistentJobQueue>();

            var provider = new Mock<IPersistentJobQueueProvider>();
            provider.Setup(x => x.GetJobQueue())
                .Returns(_queue.Object);

            _providers = new PersistentJobQueueProviderCollection(provider.Object);
        }

        private readonly PersistentJobQueueProviderCollection _providers;
        private readonly Mock<IPersistentJobQueue> _queue;

        private static _Job InsertSingleJob(IWrappedSession sql)
        {
            var newJob = new _Job
            {
                InvocationData = string.Empty,
                Arguments = string.Empty,
                CreatedAt = sql.Storage.UtcNow
            };
            sql.Insert(newJob);
            sql.Flush();
            return newJob;
        }

        private void UseJobStorageConnectionWithSession(Action<IWrappedSession, FluentNHibernateJobStorageConnection> action)
        {
            var storage = ConnectionUtils.CreateStorage();
            using (var connection = new FluentNHibernateJobStorageConnection(storage))
            {
                using (var session = storage.GetStatefulSession())
                {
                    action(session, connection);
                }
            }
        }

        private void UseJobStorageConnection(Action<FluentNHibernateJobStorageConnection> action)
        {
            var persistenceConfigurer = ConnectionUtils.CreatePersistenceConfigurer();
            
            var storage = new Mock<FluentNHibernateJobStorage>(persistenceConfigurer);
            storage.Setup(x => x.QueueProviders).Returns(_providers);

            using (var connection = new FluentNHibernateJobStorageConnection(storage.Object))
            {
                action(connection);
            }
            
        }

        public static void SampleMethod(string arg)
        {
        }

        [Fact]
        [CleanDatabase]
        public void AcquireLock_ReturnsNonNullInstance()
        {
            UseJobStorageConnection(connection =>
            {
                var @lock = connection.AcquireDistributedLock("1", TimeSpan.FromSeconds(1));
                Assert.NotNull(@lock);
            });
        }

        [Fact]
        [CleanDatabase]
        public void AnnounceServer_CreatesOrUpdatesARecord()
        {
            UseJobStorageConnectionWithSession((sql, connection) =>
            {
                var context1 = new ServerContext
                {
                    Queues = new[] {"critical", "default"},
                    WorkerCount = 4
                };
                connection.AnnounceServer("server", context1);

                var server = sql.Query<_Server>().Single();
                Assert.Equal("server", server.Id);
                Assert.True(server.Data.StartsWith(
                        "{\"WorkerCount\":4,\"Queues\":[\"critical\",\"default\"],\"StartedAt\":"),
                    server.Data);
                Assert.NotNull(server.LastHeartbeat);

                var context2 = new ServerContext
                {
                    Queues = new[] {"default"},
                    WorkerCount = 1000
                };
                connection.AnnounceServer("server", context2);
                var sameServer = sql.Query<_Server>().Single();
                Assert.Equal("server", sameServer.Id);
                Assert.Contains("1000", sameServer.Data);
            });
        }

        [Fact]
        [CleanDatabase]
        public void AnnounceServer_ThrowsAnException_WhenContextIsNull()
        {
            UseJobStorageConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentNullException>(
                    () => connection.AnnounceServer("server", null));

                Assert.Equal("context", exception.ParamName);
            });
        }

        [Fact]
        [CleanDatabase]
        public void AnnounceServer_ThrowsAnException_WhenServerIdIsNull()
        {
            UseJobStorageConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentNullException>(
                    () => connection.AnnounceServer(null, new ServerContext()));

                Assert.Equal("serverId", exception.ParamName);
            });
        }

        [Fact]
        [CleanDatabase]
        public void CreateExpiredJob_CreatesAJobInTheStorage_AndSetsItsParameters()
        {
            UseJobStorageConnectionWithSession((sql, connection) =>
            {
                var createdAt = new DateTime(2012, 12, 12);
                var jobId = connection.CreateExpiredJob(
                    Job.FromExpression(() => SampleMethod("Hello")),
                    new Dictionary<string, string> {{"Key1", "Value1"}, {"Key2", "Value2"}},
                    createdAt,
                    TimeSpan.FromDays(1));

                Assert.NotNull(jobId);
                Assert.NotEmpty(jobId);

                var sqlJob = sql.Query<_Job>().Single();
                Assert.Equal(jobId, sqlJob.Id.ToString());
                Assert.Equal(createdAt, sqlJob.CreatedAt);
                Assert.Equal(null, sqlJob.StateName);

                var invocationData = JobHelper.FromJson<InvocationData>(sqlJob.InvocationData);
                invocationData.Arguments = sqlJob.Arguments;

                var job = invocationData.Deserialize();
                Assert.Equal(typeof(FluentNHibernateStorageConnectionTests), job.Type);
                Assert.Equal("SampleMethod", job.Method.Name);
                Assert.Equal("\"Hello\"", job.Arguments[0]);

                Assert.True(createdAt.AddDays(1).AddMinutes(-1) < sqlJob.ExpireAt);
                Assert.True(sqlJob.ExpireAt < createdAt.AddDays(1).AddMinutes(1));

                var parameters = sql.Query<_JobParameter>()
                    .Where(i => i.Job.Id == int.Parse(jobId))
                    .ToDictionary(x => x.Name, x => x.Value);

                Assert.Equal("Value1", parameters["Key1"]);
                Assert.Equal("Value2", parameters["Key2"]);
            });
        }

        [Fact]
        [CleanDatabase]
        public void CreateExpiredJob_ThrowsAnException_WhenJobIsNull()
        {
            UseJobStorageConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentNullException>(
                    () => connection.CreateExpiredJob(
                        null,
                        new Dictionary<string, string>(),
                        DateTime.UtcNow,
                        TimeSpan.Zero));

                Assert.Equal("job", exception.ParamName);
            });
        }

        [Fact]
        [CleanDatabase]
        public void CreateExpiredJob_ThrowsAnException_WhenParametersCollectionIsNull()
        {
            UseJobStorageConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentNullException>(
                    () => connection.CreateExpiredJob(
                        Job.FromExpression(() => SampleMethod("hello")),
                        null,
                        DateTime.UtcNow,
                        TimeSpan.Zero));

                Assert.Equal("parameters", exception.ParamName);
            });
        }

        [Fact]
        [CleanDatabase]
        public void CreateWriteTransaction_ReturnsNonNullInstance()
        {
            UseJobStorageConnection(connection =>
            {
                var transaction = connection.CreateWriteTransaction();
                Assert.NotNull(transaction);
            });
        }

        [Fact]
        public void Ctor_ThrowsAnException_WhenStorageIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => new FluentNHibernateJobStorageConnection(null));

            Assert.Equal("storage", exception.ParamName);
        }

        [Fact]
        [CleanDatabase]
        public void FetchNextJob_DelegatesItsExecution_ToTheQueue()
        {
            UseJobStorageConnection(connection =>
            {
                var token = new CancellationToken();
                var queues = new[] {"default"};

                connection.FetchNextJob(queues, token);

                _queue.Verify(x => x.Dequeue(queues, token));
            });
        }

        [Fact]
        [CleanDatabase]
        public void FetchNextJob_Throws_IfMultipleProvidersResolved()
        {
            UseJobStorageConnection(connection =>
            {
                var token = new CancellationToken();
                var anotherProvider = new Mock<IPersistentJobQueueProvider>();
                _providers.Add(anotherProvider.Object, new[] {"critical"});

                Assert.Throws<InvalidOperationException>(
                    () => connection.FetchNextJob(new[] {"critical", "default"}, token));
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetAllEntriesFromHash_ReturnsAllKeysAndTheirValues()
        {
            const string arrangeSql = @"
insert into Hash (`Key`, `Field`, `Value`)
values (@key, @field, @value)";

            UseJobStorageConnectionWithSession((sql, connection) =>
            {
                // Arrange
                sql.Execute(arrangeSql, new[]
                {
                    new {key = "some-hash", field = "Key1", value = "Value1"},
                    new {key = "some-hash", field = "Key2", value = "Value2"},
                    new {key = "another-hash", field = "Key3", value = "Value3"}
                });

                // Act
                var result = connection.GetAllEntriesFromHash("some-hash");

                // Assert
                Assert.NotNull(result);
                Assert.Equal(2, result.Count);
                Assert.Equal("Value1", result["Key1"]);
                Assert.Equal("Value2", result["Key2"]);
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetAllEntriesFromHash_ReturnsNull_IfHashDoesNotExist()
        {
            UseJobStorageConnection(connection =>
            {
                var result = connection.GetAllEntriesFromHash("some-hash");
                Assert.Null(result);
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetAllEntriesFromHash_ThrowsAnException_WhenKeyIsNull()
        {
            UseJobStorageConnection(connection =>
                Assert.Throws<ArgumentNullException>(() => connection.GetAllEntriesFromHash(null)));
        }

        [Fact]
        [CleanDatabase]
        public void GetAllItemsFromList_ReturnsAllItems_FromAGivenList()
        {
            const string arrangeSql = @"
insert into List (`Key`, Value)
values (@key, @value)";

            UseJobStorageConnectionWithSession((sql, connection) =>
            {
                // Arrange
                sql.Execute(arrangeSql, new[]
                {
                    new {key = "list-1", value = "1"},
                    new {key = "list-2", value = "2"},
                    new {key = "list-1", value = "3"}
                });

                // Act
                var result = connection.GetAllItemsFromList("list-1");

                // Assert
                Assert.Equal(new[] {"3", "1"}, result);
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetAllItemsFromList_ReturnsAnEmptyList_WhenListDoesNotExist()
        {
            UseJobStorageConnection(connection =>
            {
                var result = connection.GetAllItemsFromList("my-list");
                Assert.Empty(result);
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetAllItemsFromList_ThrowsAnException_WhenKeyIsNull()
        {
            UseJobStorageConnection(connection =>
            {
                Assert.Throws<ArgumentNullException>(
                    () => connection.GetAllItemsFromList(null));
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetAllItemsFromSet_ReturnsAllItems()
        {
            const string arrangeSql = @"
insert into `Set` (`Key`, Score, Value)
values (@key, 0.0, @value)";

            UseJobStorageConnectionWithSession((sql, connection) =>
            {
                // Arrange
                sql.Execute(arrangeSql, new[]
                {
                    new {key = "some-set", value = "1"},
                    new {key = "some-set", value = "2"},
                    new {key = "another-set", value = "3"}
                });

                // Act
                var result = connection.GetAllItemsFromSet("some-set");

                // Assert
                Assert.Equal(2, result.Count);
                Assert.Contains("1", result);
                Assert.Contains("2", result);
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetAllItemsFromSet_ReturnsEmptyCollection_WhenKeyDoesNotExist()
        {
            UseJobStorageConnection(connection =>
            {
                var result = connection.GetAllItemsFromSet("some-set");

                Assert.NotNull(result);
                Assert.Equal(0, result.Count);
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetAllItemsFromSet_ThrowsAnException_WhenKeyIsNull()
        {
            UseJobStorageConnection(connection =>
                Assert.Throws<ArgumentNullException>(() => connection.GetAllItemsFromSet(null)));
        }

        [Fact]
        [CleanDatabase]
        public void GetCounter_IncludesValues_FromCounterAggregateTable()
        {
            const string arrangeSql = @"
insert into AggregatedCounter (`Key`, `Value`)
values (@key, @value)";

            UseJobStorageConnectionWithSession((sql, connection) =>
            {
                // Arrange
                sql.Execute(arrangeSql, new[]
                {
                    new {key = "counter-1", value = 12},
                    new {key = "counter-2", value = 15}
                });

                // Act
                var result = connection.GetCounter("counter-1");

                Assert.Equal(12, result);
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetCounter_ReturnsSumOfValues_InCounterTable()
        {
            const string arrangeSql = @"
insert into Counter (`Key`, `Value`)
values (@key, @value)";

            UseJobStorageConnectionWithSession((sql, connection) =>
            {
                // Arrange
                sql.Execute(arrangeSql, new[]
                {
                    new {key = "counter-1", value = 1},
                    new {key = "counter-2", value = 1},
                    new {key = "counter-1", value = 1}
                });

                // Act
                var result = connection.GetCounter("counter-1");

                // Assert
                Assert.Equal(2, result);
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetCounter_ReturnsZero_WhenKeyDoesNotExist()
        {
            UseJobStorageConnection(connection =>
            {
                var result = connection.GetCounter("my-counter");
                Assert.Equal(0, result);
            });
        }


        [Fact]
        [CleanDatabase]
        public void GetCounter_ThrowsAnException_WhenKeyIsNull()
        {
            UseJobStorageConnection(connection =>
            {
                Assert.Throws<ArgumentNullException>(
                    () => connection.GetCounter(null));
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetFirstByLowestScoreFromSet_ReturnsNull_WhenTheKeyDoesNotExist()
        {
            UseJobStorageConnection(connection =>
            {
                var result = connection.GetFirstByLowestScoreFromSet(
                    "key", 0, 1);

                Assert.Null(result);
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetFirstByLowestScoreFromSet_ReturnsTheValueWithTheLowestScore()
        {
            UseJobStorageConnectionWithSession((sql, connection) =>
            {
                sql.Insert(new _Set {Key = "key", Score = 1, Value = "1.0"});
                sql.Insert(new _Set {Key = "key", Score = -1, Value = "-1.0"});
                sql.Insert(new _Set {Key = "key", Score = -5, Value = "-5.0"});
                sql.Insert(new _Set {Key = "another-key", Score = -2, Value = "-2.0"});
                sql.Flush();

                var result = connection.GetFirstByLowestScoreFromSet("key", -1.0, 3.0);

                Assert.Equal("-1.0", result);
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetFirstByLowestScoreFromSet_ThrowsAnException_ToScoreIsLowerThanFromScore()
        {
            UseJobStorageConnection(connection => Assert.Throws<ArgumentException>(
                () => connection.GetFirstByLowestScoreFromSet("key", 0, -1)));
        }

        [Fact]
        [CleanDatabase]
        public void GetFirstByLowestScoreFromSet_ThrowsAnException_WhenKeyIsNull()
        {
            UseJobStorageConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentNullException>(
                    () => connection.GetFirstByLowestScoreFromSet(null, 0, 1));

                Assert.Equal("key", exception.ParamName);
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetHashCount_ReturnsNumber_OfHashFields()
        {
            const string arrangeSql = @"
insert into Hash (`Key`, `Field`)
values (@key, @field)";

            UseJobStorageConnectionWithSession((sql, connection) =>
            {
                // Arrange
                sql.Execute(arrangeSql, new[]
                {
                    new {key = "hash-1", field = "field-1"},
                    new {key = "hash-1", field = "field-2"},
                    new {key = "hash-2", field = "field-1"}
                });

                // Act
                var result = connection.GetHashCount("hash-1");

                // Assert
                Assert.Equal(2, result);
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetHashCount_ReturnsZero_WhenKeyDoesNotExist()
        {
            UseJobStorageConnection(connection =>
            {
                var result = connection.GetHashCount("my-hash");
                Assert.Equal(0, result);
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetHashCount_ThrowsAnException_WhenKeyIsNull()
        {
            UseJobStorageConnection(connection => { Assert.Throws<ArgumentNullException>(() => connection.GetHashCount(null)); });
        }

        [Fact]
        [CleanDatabase]
        public void GetHashTtl_ReturnsExpirationTimeForHash()
        {
            const string arrangeSql = @"
insert into Hash (`Key`, `Field`, `ExpireAt`)
values (@key, @field, @expireAt)";

            UseJobStorageConnectionWithSession((sql, connection) =>
            {
                // Arrange
                sql.Execute(arrangeSql, new[]
                {
                    new {key = "hash-1", field = "field", expireAt = (DateTime?) DateTime.UtcNow.AddHours(1)},
                    new {key = "hash-2", field = "field", expireAt = (DateTime?) null}
                });

                // Act
                var result = connection.GetHashTtl("hash-1");

                // Assert
                Assert.True(TimeSpan.FromMinutes(59) < result);
                Assert.True(result < TimeSpan.FromMinutes(61));
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetHashTtl_ReturnsNegativeValue_WhenHashDoesNotExist()
        {
            UseJobStorageConnection(connection =>
            {
                var result = connection.GetHashTtl("my-hash");
                Assert.True(result < TimeSpan.Zero);
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetHashTtl_ThrowsAnException_WhenKeyIsNull()
        {
            UseJobStorageConnection(connection =>
            {
                Assert.Throws<ArgumentNullException>(
                    () => connection.GetHashTtl(null));
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetJobData_ReturnsJobLoadException_IfThereWasADeserializationException()
        {
            UseJobStorageConnectionWithSession((sql, connection) =>
            {
                var newJob = new _Job
                {
                    InvocationData = JobHelper.ToJson(new InvocationData(null, null, null, null)),
                    StateName = "Succeeded",
                    Arguments = "['Arguments']"
                };
                sql.Insert(newJob);
                sql.Flush();

                var result = connection.GetJobData(newJob.Id.ToString());

                Assert.NotNull(result.LoadException);
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetJobData_ReturnsNull_WhenThereIsNoSuchJob()
        {
            UseJobStorageConnection(connection =>
            {
                var result = connection.GetJobData("1");
                Assert.Null(result);
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetJobData_ReturnsResult_WhenJobExists()
        {
            UseJobStorageConnectionWithSession((sql, connection) =>
            {
                var job = Job.FromExpression(() => SampleMethod("wrong"));
                var newJob = new _Job
                {
                    InvocationData = JobHelper.ToJson(InvocationData.Serialize(job)),
                    StateName = "Succeeded",
                    Arguments = "['Arguments']"
                };
                sql.Insert(newJob);
                var jobId = newJob.Id;

                var result = connection.GetJobData(jobId.ToString());

                Assert.NotNull(result);
                Assert.NotNull(result.Job);
                Assert.Equal("Succeeded", result.State);
                Assert.Equal("Arguments", result.Job.Args[0]);
                Assert.Null(result.LoadException);
                Assert.True(DateTime.UtcNow.AddMinutes(-1) < result.CreatedAt);
                Assert.True(result.CreatedAt < DateTime.UtcNow.AddMinutes(1));
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetJobData_ThrowsAnException_WhenJobIdIsNull()
        {
            UseJobStorageConnection(connection => Assert.Throws<ArgumentNullException>(
                () => connection.GetJobData(null)));
        }

        [Fact]
        [CleanDatabase]
        public void GetListCount_ReturnsTheNumberOfListElements()
        {
            const string arrangeSql = @"
insert into List (`Key`)
values (@key)";

            UseJobStorageConnectionWithSession((sql, connection) =>
            {
                // Arrange
                sql.Execute(arrangeSql, new[]
                {
                    new {key = "list-1"},
                    new {key = "list-1"},
                    new {key = "list-2"}
                });

                // Act
                var result = connection.GetListCount("list-1");

                // Assert
                Assert.Equal(2, result);
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetListCount_ReturnsZero_WhenListDoesNotExist()
        {
            UseJobStorageConnection(connection =>
            {
                var result = connection.GetListCount("my-list");
                Assert.Equal(0, result);
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetListCount_ThrowsAnException_WhenKeyIsNull()
        {
            UseJobStorageConnection(connection =>
            {
                Assert.Throws<ArgumentNullException>(
                    () => connection.GetListCount(null));
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetListTtl_ReturnsExpirationTimeForList()
        {
            const string arrangeSql = @"
insert into List (`Key`, `ExpireAt`)
values (@key, @expireAt)";

            UseJobStorageConnectionWithSession((sql, connection) =>
            {
                // Arrange
                sql.Execute(arrangeSql, new[]
                {
                    new {key = "list-1", expireAt = (DateTime?) DateTime.UtcNow.AddHours(1)},
                    new {key = "list-2", expireAt = (DateTime?) null}
                });

                // Act
                var result = connection.GetListTtl("list-1");

                // Assert
                Assert.True(TimeSpan.FromMinutes(59) < result);
                Assert.True(result < TimeSpan.FromMinutes(61));
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetListTtl_ReturnsNegativeValue_WhenListDoesNotExist()
        {
            UseJobStorageConnection(connection =>
            {
                var result = connection.GetListTtl("my-list");
                Assert.True(result < TimeSpan.Zero);
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetListTtl_ThrowsAnException_WhenKeyIsNull()
        {
            UseJobStorageConnection(connection =>
            {
                Assert.Throws<ArgumentNullException>(
                    () => connection.GetListTtl(null));
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetParameter_ReturnsNull_WhenParameterDoesNotExists()
        {
            UseJobStorageConnection(connection =>
            {
                var value = connection.GetJobParameter("1", "hello");
                Assert.Null(value);
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetParameter_ReturnsParameterValue_WhenJobExists()
        {
            UseJobStorageConnectionWithSession((sql, connection) =>
            {
                var newJob = InsertSingleJob(sql);
                sql.Insert(new _JobParameter {Job = newJob, Name = "name", Value = "value"});
                sql.Flush();

                var value = connection.GetJobParameter(newJob.Id.ToString(), "name");

                Assert.Equal("value", value);
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetParameter_ThrowsAnException_WhenJobIdIsNull()
        {
            UseJobStorageConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentNullException>(
                    () => connection.GetJobParameter(null, "hello"));

                Assert.Equal("id", exception.ParamName);
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetParameter_ThrowsAnException_WhenNameIsNull()
        {
            UseJobStorageConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentNullException>(
                    () => connection.GetJobParameter("1", null));

                Assert.Equal("name", exception.ParamName);
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetRangeFromList_ReturnsAllEntries_WithinGivenBounds()
        {
            const string arrangeSql = @"
insert into List (`Key`, `Value`)
values (@key, @value)";

            UseJobStorageConnectionWithSession((sql, connection) =>
            {
                // Arrange
                sql.Execute(arrangeSql, new[]
                {
                    new {key = "list-1", value = "1"},
                    new {key = "list-2", value = "2"},
                    new {key = "list-1", value = "3"},
                    new {key = "list-1", value = "4"},
                    new {key = "list-1", value = "5"}
                });

                // Act
                var result = connection.GetRangeFromList("list-1", 1, 2);

                // Assert
                Assert.Equal(new[] {"4", "3"}, result);
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetRangeFromList_ReturnsAnEmptyList_WhenListDoesNotExist()
        {
            UseJobStorageConnection(connection =>
            {
                var result = connection.GetRangeFromList("my-list", 0, 1);
                Assert.Empty(result);
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetRangeFromList_ThrowsAnException_WhenKeyIsNull()
        {
            UseJobStorageConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentNullException>(
                    () => connection.GetRangeFromList(null, 0, 1));

                Assert.Equal("key", exception.ParamName);
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetRangeFromSet_ReturnsPagedElements()
        {
            const string arrangeSql = @"
insert into `Set` (`Key`, `Value`, `Score`)
values (@Key, @Value, 0.0)";

            UseJobStorageConnectionWithSession((sql, connection) =>
            {
                sql.Execute(arrangeSql, new List<dynamic>
                {
                    new {Key = "set-1", Value = "1"},
                    new {Key = "set-1", Value = "2"},
                    new {Key = "set-1", Value = "3"},
                    new {Key = "set-1", Value = "4"},
                    new {Key = "set-2", Value = "4"},
                    new {Key = "set-1", Value = "5"}
                });

                var result = connection.GetRangeFromSet("set-1", 2, 3);

                Assert.Equal(new[] {"3", "4"}, result);
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetRangeFromSet_ReturnsPagedElements2()
        {
            const string arrangeSql = @"
insert into `Set` (`Key`, `Value`, `Score`)
values (@Key, @Value, 0.0)";

            UseJobStorageConnectionWithSession((sql, connection) =>
            {
                sql.Execute(arrangeSql, new List<dynamic>
                {
                    new {Key = "set-1", Value = "1"},
                    new {Key = "set-1", Value = "2"},
                    new {Key = "set-0", Value = "3"},
                    new {Key = "set-1", Value = "4"},
                    new {Key = "set-2", Value = "1"},
                    new {Key = "set-1", Value = "5"},
                    new {Key = "set-2", Value = "2"},
                    new {Key = "set-1", Value = "3"}
                });

                var result = connection.GetRangeFromSet("set-1", 0, 4);

                Assert.Equal(new[] {"1", "2", "4", "5", "3"}, result);
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetRangeFromSet_ThrowsAnException_WhenKeyIsNull()
        {
            UseJobStorageConnection(connection =>
            {
                Assert.Throws<ArgumentNullException>(() => connection.GetRangeFromSet(null, 0, 1));
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetSetCount_ReturnsNumberOfElements_InASet()
        {
            const string arrangeSql = @"
insert into `Set` (`Key`, `Value`, `Score`)
values (@key, @value, 0.0)";

            UseJobStorageConnectionWithSession((sql, connection) =>
            {
                sql.Execute(arrangeSql, new List<dynamic>
                {
                    new {key = "set-1", value = "value-1"},
                    new {key = "set-2", value = "value-1"},
                    new {key = "set-1", value = "value-2"}
                });

                var result = connection.GetSetCount("set-1");

                Assert.Equal(2, result);
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetSetCount_ReturnsZero_WhenSetDoesNotExist()
        {
            UseJobStorageConnection(connection =>
            {
                var result = connection.GetSetCount("my-set");
                Assert.Equal(0, result);
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetSetCount_ThrowsAnException_WhenKeyIsNull()
        {
            UseJobStorageConnection(connection =>
            {
                Assert.Throws<ArgumentNullException>(
                    () => connection.GetSetCount(null));
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetSetTtl_ReturnsExpirationTime_OfAGivenSet()
        {
            const string arrangeSql = @"
insert into `Set` (`Key`, `Value`, `ExpireAt`, `Score`)
values (@key, @value, @expireAt, 0.0)";

            UseJobStorageConnectionWithSession((sql, connection) =>
            {
                // Arrange
                sql.Execute(arrangeSql, new[]
                {
                    new {key = "set-1", value = "1", expireAt = (DateTime?) DateTime.UtcNow.AddMinutes(60)},
                    new {key = "set-2", value = "2", expireAt = (DateTime?) null}
                });

                // Act
                var result = connection.GetSetTtl("set-1");

                // Assert
                Assert.True(TimeSpan.FromMinutes(59) < result);
                Assert.True(result < TimeSpan.FromMinutes(61));
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetSetTtl_ReturnsNegativeValue_WhenSetDoesNotExist()
        {
            UseJobStorageConnection(connection =>
            {
                var result = connection.GetSetTtl("my-set");
                Assert.True(result < TimeSpan.Zero);
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetSetTtl_ThrowsAnException_WhenKeyIsNull()
        {
            UseJobStorageConnection(connection => { Assert.Throws<ArgumentNullException>(() => connection.GetSetTtl(null)); });
        }

        [Fact]
        [CleanDatabase]
        public void GetStateData_ReturnsCorrectData()
        {
            UseJobStorageConnectionWithSession((sql, connection) =>
            {
                var data = new Dictionary<string, string>
                {
                    {"Key", "Value"}
                };
                var newJob = new _Job
                {
                    InvocationData = string.Empty,
                    Arguments = string.Empty,
                    StateName = string.Empty,
                    CreatedAt = sql.Storage.UtcNow
                };
                sql.Insert(newJob);
                sql.Insert(new _JobState {Job = newJob, Name = "old-state", CreatedAt = sql.Storage.UtcNow});
                var lastState = new _JobState
                {
                    Job = newJob,
                    Name = "Name",
                    Reason = "Reason",
                    CreatedAt = sql.Storage.UtcNow,
                    Data = JobHelper.ToJson(data)
                };
                sql.Insert(lastState);
                sql.Flush();
                newJob.StateName = lastState.Name;
                newJob.StateReason = lastState.Reason;
                newJob.StateData = lastState.Data;
                newJob.LastStateChangedAt = sql.Storage.UtcNow;
                sql.Update(newJob);
                sql.Flush();


                var jobId = newJob.Id;

                var result = connection.GetStateData(jobId.ToString());
                Assert.NotNull(result);

                Assert.Equal("Name", result.Name);
                Assert.Equal("Reason", result.Reason);
                Assert.Equal("Value", result.Data["Key"]);
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetStateData_ReturnsCorrectData_WhenPropertiesAreCamelcased()
        {
            const string arrangeSql = @"
insert into Job (InvocationData, Arguments, StateName, CreatedAt)
values ('', '', '', UTC_TIMESTAMP());
select last_insert_id() into @JobId;
insert into State (JobId, Name, CreatedAt)
values (@JobId, 'old-state', UTC_TIMESTAMP());
insert into State (JobId, Name, Reason, Data, CreatedAt)
values (@JobId, @name, @reason, @data, UTC_TIMESTAMP());
select last_insert_id() into @StateId;
update Job set StateId = @StateId;
select @JobId as Id;";

            UseJobStorageConnectionWithSession((sql, connection) =>
            {
                var data = new Dictionary<string, string>
                {
                    {"key", "Value"}
                };

                var jobId = (int) sql.Query(
                        arrangeSql,
                        new {name = "Name", reason = "Reason", data = JobHelper.ToJson(data)})
                    .Single()
                    .Id;

                var result = connection.GetStateData(jobId.ToString());
                Assert.NotNull(result);

                Assert.Equal("Value", result.Data["Key"]);
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetStateData_ReturnsNull_IfThereIsNoSuchState()
        {
            UseJobStorageConnection(connection =>
            {
                var result = connection.GetStateData("1");
                Assert.Null(result);
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetStateData_ThrowsAnException_WhenJobIdIsNull()
        {
            UseJobStorageConnection(
                connection => Assert.Throws<ArgumentNullException>(
                    () => connection.GetStateData(null)));
        }

        [Fact]
        [CleanDatabase]
        public void GetValueFromHash_ReturnsNull_WhenHashDoesNotExist()
        {
            UseJobStorageConnection(connection =>
            {
                var result = connection.GetValueFromHash("my-hash", "name");
                Assert.Null(result);
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetValueFromHash_ReturnsValue_OfAGivenField()
        {
            const string arrangeSql = @"
insert into Hash (`Key`, `Field`, `Value`)
values (@key, @field, @value)";

            UseJobStorageConnectionWithSession((sql, connection) =>
            {
                // Arrange
                sql.Execute(arrangeSql, new[]
                {
                    new {key = "hash-1", field = "field-1", value = "1"},
                    new {key = "hash-1", field = "field-2", value = "2"},
                    new {key = "hash-2", field = "field-1", value = "3"}
                });

                // Act
                var result = connection.GetValueFromHash("hash-1", "field-1");

                // Assert
                Assert.Equal("1", result);
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetValueFromHash_ThrowsAnException_WhenKeyIsNull()
        {
            UseJobStorageConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentNullException>(
                    () => connection.GetValueFromHash(null, "name"));

                Assert.Equal("key", exception.ParamName);
            });
        }

        [Fact]
        [CleanDatabase]
        public void GetValueFromHash_ThrowsAnException_WhenNameIsNull()
        {
            UseJobStorageConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentNullException>(
                    () => connection.GetValueFromHash("key", null));

                Assert.Equal("name", exception.ParamName);
            });
        }

        [Fact]
        [CleanDatabase]
        public void Heartbeat_ThrowsAnException_WhenServerIdIsNull()
        {
            UseJobStorageConnection(connection => Assert.Throws<ArgumentNullException>(
                () => connection.Heartbeat(null)));
        }

        [Fact]
        [CleanDatabase]
        public void Heartbeat_UpdatesLastHeartbeat_OfTheServerWithGivenId()
        {
            const string arrangeSql = @"
insert into Server (Id, Data, LastHeartbeat)
values
('server1', '', '2012-12-12 12:12:12'),
('server2', '', '2012-12-12 12:12:12')";

            UseJobStorageConnectionWithSession((sql, connection) =>
            {
                sql.Execute(arrangeSql);

                connection.Heartbeat("server1");

                var servers = sql.Query<_Server>()
                    .ToDictionary(x => x.Id, x => x.LastHeartbeat);

                Assert.NotEqual(2012, servers["server1"].Year);
                Assert.Equal(2012, servers["server2"].Year);
            });
        }

        [Fact]
        [CleanDatabase]
        public void RemoveServer_RemovesAServerRecord()
        {
            UseJobStorageConnectionWithSession((sql, connection) =>
            {
                sql.Insert(new _Server {Id = "Server1", Data = string.Empty, LastHeartbeat = sql.Storage.UtcNow});
                sql.Insert(new _Server {Id = "Server2", Data = string.Empty, LastHeartbeat = sql.Storage.UtcNow});
                sql.Flush();

                connection.RemoveServer("Server1");

                var server = sql.Query<_Server>().Single();
                Assert.NotEqual("Server1", server.Id, StringComparer.OrdinalIgnoreCase);
            });
        }

        [Fact]
        [CleanDatabase]
        public void RemoveServer_ThrowsAnException_WhenServerIdIsNull()
        {
            UseJobStorageConnection(connection => Assert.Throws<ArgumentNullException>(
                () => connection.RemoveServer(null)));
        }

        [Fact]
        [CleanDatabase]
        public void RemoveTimedOutServers_DoItsWorkPerfectly()
        {
            const string arrangeSql = @"
insert into Server (Id, Data, LastHeartbeat)
values (@id, '', @heartbeat)";

            UseJobStorageConnectionWithSession((sql, connection) =>
            {
                sql.Execute(
                    arrangeSql,
                    new[]
                    {
                        new {id = "server1", heartbeat = DateTime.UtcNow.AddDays(-1)},
                        new {id = "server2", heartbeat = DateTime.UtcNow.AddHours(-12)}
                    });

                connection.RemoveTimedOutServers(TimeSpan.FromHours(15));

                var liveServer = sql.Query<_Server>().Single();
                Assert.Equal("server2", liveServer.Id);
            });
        }

        [Fact]
        [CleanDatabase]
        public void RemoveTimedOutServers_ThrowsAnException_WhenTimeOutIsNegative()
        {
            UseJobStorageConnection(connection => Assert.Throws<ArgumentException>(
                () => connection.RemoveTimedOutServers(TimeSpan.FromMinutes(-5))));
        }

        [Fact]
        [CleanDatabase]
        public void SetParameter_CanAcceptNulls_AsValues()
        {
            UseJobStorageConnectionWithSession((sql, connection) =>
            {
                var newJob = InsertSingleJob(sql);
                var jobId = newJob.Id.ToString();

                connection.SetJobParameter(jobId, "Name", null);

                var parameter = sql.Query<_JobParameter>().Single(i => i.Job == newJob && i.Name == "Name");

                Assert.Equal(null, parameter.Value);
            });
        }

        [Fact]
        [CleanDatabase]
        public void SetParameter_ThrowsAnException_WhenJobIdIsNull()
        {
            UseJobStorageConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentNullException>(
                    () => connection.SetJobParameter(null, "name", "value"));

                Assert.Equal("id", exception.ParamName);
            });
        }

        [Fact]
        [CleanDatabase]
        public void SetParameter_ThrowsAnException_WhenNameIsNull()
        {
            UseJobStorageConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentNullException>(
                    () => connection.SetJobParameter("1", null, "value"));

                Assert.Equal("name", exception.ParamName);
            });
        }

        [Fact]
        [CleanDatabase]
        public void SetParameter_UpdatesValue_WhenParameterWithTheGivenName_AlreadyExists()
        {
            UseJobStorageConnectionWithSession((sql, connection) =>
            {
                var newJob = InsertSingleJob(sql);
                var jobId = newJob.Id.ToString();

                connection.SetJobParameter(jobId, "Name", "Value");
                connection.SetJobParameter(jobId, "Name", "AnotherValue");

                var parameter = sql.Query<_JobParameter>().Single(i => i.Job == newJob && i.Name == "Name");

                Assert.Equal("AnotherValue", parameter.Value);
            });
        }

        [Fact]
        [CleanDatabase]
        public void SetParameters_CreatesNewParameter_WhenParameterWithTheGivenNameDoesNotExists()
        {
            UseJobStorageConnectionWithSession((sql, connection) =>
            {
                var newJob = InsertSingleJob(sql);

                var jobId = newJob.Id.ToString();

                connection.SetJobParameter(jobId, "Name", "Value");

                var parameter = sql.Query<_JobParameter>().Single(i => i.Job == newJob && i.Name == "Name");

                Assert.Equal("Value", parameter.Value);
            });
        }

        [Fact]
        [CleanDatabase]
        public void SetRangeInHash_MergesAllRecords()
        {
            UseJobStorageConnectionWithSession((sql, connection) =>
            {
                connection.SetRangeInHash("some-hash", new Dictionary<string, string>
                {
                    {"Key1", "Value1"},
                    {"Key2", "Value2"}
                });

                var result = sql.Query(
                        "select * from Hash where `Key` = @key",
                        new {key = "some-hash"})
                    .ToDictionary(x => (string) x.Field, x => (string) x.Value);

                Assert.Equal("Value1", result["Key1"]);
                Assert.Equal("Value2", result["Key2"]);
            });
        }

        [Fact]
        [CleanDatabase]
        public void SetRangeInHash_ThrowsAnException_WhenKeyIsNull()
        {
            UseJobStorageConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentNullException>(
                    () => connection.SetRangeInHash(null, new Dictionary<string, string>()));

                Assert.Equal("key", exception.ParamName);
            });
        }

        [Fact]
        [CleanDatabase]
        public void SetRangeInHash_ThrowsAnException_WhenKeyValuePairsArgumentIsNull()
        {
            UseJobStorageConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentNullException>(
                    () => connection.SetRangeInHash("some-hash", null));

                Assert.Equal("keyValuePairs", exception.ParamName);
            });
        }
    }
}