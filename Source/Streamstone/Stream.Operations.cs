﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Table;

namespace Streamstone
{
    public sealed partial class Stream
    {
        abstract class StreamOperation
        {
            protected readonly Stream Stream;
            protected readonly CloudTable Table;

            protected StreamOperation(Stream stream)
            {
                Requires.NotNull(stream, "stream");
                
                Stream = stream;
                Table  = stream.Partition.Table;
            }
        }

        class ProvisionOperation : StreamOperation
        {
            public ProvisionOperation(Stream stream) : base(stream)
            {
                Debug.Assert(stream.IsTransient);
            }

            public Stream Execute()
            {
                var insert = new Insert(Stream);

                try
                {
                    Table.Execute(insert.Prepare());
                }
                catch (StorageException e)
                {
                    insert.Handle(Table, e);
                }

                return insert.Result();
            }

            public async Task<Stream> ExecuteAsync()
            {
                var insert = new Insert(Stream);

                try
                {
                    await Table.ExecuteAsync(insert.Prepare()).Really();
                }
                catch (StorageException e)
                {
                    insert.Handle(Table, e);
                }

                return insert.Result();
            }

            class Insert
            {
                readonly StreamEntity stream;
                readonly Partition partition;

                public Insert(Stream stream)
                {
                    this.stream = stream.Entity();
                    partition = stream.Partition;
                }

                public TableOperation Prepare()
                {
                    return TableOperation.Insert(stream);
                }

                internal void Handle(CloudTable table, StorageException exception)
                {
                    if (exception.RequestInformation.HttpStatusCode == (int)HttpStatusCode.Conflict)
                        throw ConcurrencyConflictException.StreamChangedOrExists(table, partition);

                    throw exception.PreserveStackTrace();
                }

                internal Stream Result()
                {
                    return From(partition, stream);
                }
            }
        }

        class WriteOperation : StreamOperation
        {
            readonly EventData[] events;
            readonly bool ded;

            public WriteOperation(Stream stream, EventData[] events, bool ded = true) : base(stream)
            {
                Requires.NotNull(events, "events");

                if (events.Length == 0)
                    throw new ArgumentOutOfRangeException("events", "Events have 0 items");

                this.events = events;
                this.ded = ded;
            }

            public StreamWriteResult Execute()
            {
                var batch = new Batch(Stream, events, ded);
                
                try
                {
                    Table.ExecuteBatch(batch.Prepare());
                }
                catch (StorageException e)
                {
                    batch.Handle(Table, e);
                }

                return batch.Result();
            }

            public async Task<StreamWriteResult> ExecuteAsync()
            {
                var batch = new Batch(Stream, events, ded);
                
                try
                {
                    await Table.ExecuteBatchAsync(batch.Prepare()).Really();
                }
                catch (StorageException e)
                {
                    batch.Handle(Table, e);
                }

                return batch.Result();
            }

            class Batch
            {
                readonly TableBatchOperation operations = new TableBatchOperation();
                readonly List<ITableEntity> entities = new List<ITableEntity>();

                readonly StreamEntity stream;
                readonly RecordedEvent[] events;
                readonly Partition partition;
                readonly bool ded;

                internal Batch(Stream stream, ICollection<EventData> events, bool ded)
                {
                    this.stream = stream.Entity();
                    this.ded = ded;
                    this.partition = stream.Partition;
                    this.stream.Version = stream.Version + events.Count;
                    this.events = events
                        .Select((e, i) => e.Record(stream.Version + i + 1))
                        .ToArray();
                }

                internal TableBatchOperation Prepare()
                {
                    WriteStream();
                    WriteEvents();

                    return operations;
                }

                void WriteStream()
                {
                    if (stream.IsTransient())
                        operations.Insert(stream);
                    else
                        operations.Replace(stream);

                    entities.Add(stream);
                }

                void WriteEvents()
                {
                    foreach (var e in events)
                    {
                        WriteEvent(e.EventEntity(partition));
                        WriteId(e.IdEntity(partition));

                        foreach (var include in e.Includes)
                            WriteInclude(include);
                    }
                }

                void WriteEvent(EventEntity entity)
                {
                    operations.Insert(entity);
                    entities.Add(entity);
                }

                void WriteId(EventIdEntity entity)
                {
                    if (!ded)
                        return;

                    operations.Insert(entity);
                    entities.Add(entity);
                }

                void WriteInclude(Include include)
                {
                    operations.Add(include.Apply(partition));
                    entities.Add(include.Entity);
                }

                internal StreamWriteResult Result()
                {
                    return new StreamWriteResult(From(partition, stream), events);
                }

                internal void Handle(CloudTable table, StorageException exception)
                {
                    if (exception.RequestInformation.HttpStatusCode == (int)HttpStatusCode.PreconditionFailed)
                        throw ConcurrencyConflictException.StreamChangedOrExists(table, partition);

                    if (exception.RequestInformation.HttpStatusCode != (int)HttpStatusCode.Conflict)
                        throw exception.PreserveStackTrace();

                    var error = exception.RequestInformation.ExtendedErrorInformation;
                    if (error.ErrorCode != "EntityAlreadyExists")
                        throw UnexpectedStorageResponseException.ErrorCodeShouldBeEntityAlreadyExists(error);

                    var position = ParseConflictingEntityPosition(error);
                    Debug.Assert(position >= 0 && position < entities.Count);

                    var conflicting = entities[position];
                    if (conflicting is EventIdEntity)
                    {
                        var duplicate = events[(position - 1) / 2];
                        throw new DuplicateEventException(table, partition, duplicate.Id);
                    }

                    var @event = conflicting as EventEntity;
                    if (@event != null)
                        throw ConcurrencyConflictException.EventVersionExists(table, partition, @event.Version);

                    var include = events.SelectMany(e => e.Includes).SingleOrDefault(x => x.Entity == conflicting);
                    if (include != null)
                        throw new IncludedOperationConflictException(table, partition, include);

                    throw new WarningException("How did this happen? We've got conflict on entity which is neither event nor id or include");
                }

                static int ParseConflictingEntityPosition(StorageExtendedErrorInformation error)
                {
                    var lines = error.ErrorMessage.Split('\n');
                    if (lines.Length != 3)
                        throw UnexpectedStorageResponseException.ConflictExceptionMessageShouldHaveExactlyThreeLines(error);

                    var semicolonIndex = lines[0].IndexOf(":", StringComparison.Ordinal);
                    if (semicolonIndex == -1)
                        throw UnexpectedStorageResponseException.ConflictExceptionMessageShouldHaveSemicolonOnFirstLine(error);

                    int position;
                    if (!int.TryParse(lines[0].Substring(0, semicolonIndex), out position))
                        throw UnexpectedStorageResponseException.UnableToParseTextBeforeSemicolonToInteger(error);

                    return position;
                }
            }
        }

        class SetPropertiesOperation : StreamOperation
        {
            readonly StreamProperties properties;

            public SetPropertiesOperation(Stream stream, StreamProperties properties) : base(stream)
            {
                Requires.NotNull(properties, "properties");

                if (stream.IsTransient)
                    throw new ArgumentException("Can't set properties on transient stream", "stream");

                this.properties = properties;                
            }

            public Stream Execute()
            {
                var replace = new Replace(Stream, properties);

                try
                {
                    Table.Execute(replace.Prepare());
                }
                catch (StorageException e)
                {
                    replace.Handle(Table, e);
                }

                return replace.Result();
            }

            public async Task<Stream> ExecuteAsync()
            {
                var replace = new Replace(Stream, properties);

                try
                {
                    await Table.ExecuteAsync(replace.Prepare()).Really();
                }
                catch (StorageException e)
                {
                    replace.Handle(Table, e);
                }

                return replace.Result();
            }

            class Replace
            {
                readonly StreamEntity stream;
                readonly Partition partition;

                public Replace(Stream stream, StreamProperties properties)
                {
                    this.stream = stream.Entity();
                    this.stream.Properties = properties;
                    partition = stream.Partition;
                }

                internal TableOperation Prepare()
                {                    
                    return TableOperation.Replace(stream);
                }

                internal void Handle(CloudTable table, StorageException exception)
                {
                    if (exception.RequestInformation.HttpStatusCode == (int)HttpStatusCode.PreconditionFailed)
                        throw ConcurrencyConflictException.StreamChanged(table, partition);

                    throw exception.PreserveStackTrace();
                }

                internal Stream Result()
                {
                    return From(partition, stream);
                }
            }
        }

        abstract class PartitionOperation
        {
            protected readonly Partition Partition;
            protected readonly CloudTable Table;

            protected PartitionOperation(Partition partition)
            {
                Requires.NotNull(partition, "partition");

                Partition = partition;
                Table = partition.Table;
            }
        }

        class OpenStreamOperation : PartitionOperation
        {
            public OpenStreamOperation(Partition partition) 
                : base(partition)
            {}

            public StreamOpenResult Execute()
            {
                return Result(Table.Execute(Prepare()));
            }

            public async Task<StreamOpenResult> ExecuteAsync()
            {
                return Result(await Table.ExecuteAsync(Prepare()));
            }

            TableOperation Prepare()
            {
                return TableOperation.Retrieve<StreamEntity>(Partition.PartitionKey, Partition.StreamRowKey());
            }

            StreamOpenResult Result(TableResult result)
            {
                var entity = result.Result;

                return entity != null
                           ? new StreamOpenResult(true, From(Partition, (StreamEntity)entity))
                           : StreamOpenResult.NotFound;
            }
        }

        class ReadOperation<T> : PartitionOperation where T : class, new()
        {
            readonly int startVersion;
            readonly int sliceSize;

            public ReadOperation(Partition partition, int startVersion, int sliceSize) : base(partition)
            {
                Requires.GreaterThanOrEqualToOne(startVersion, "startVersion");
                Requires.GreaterThanOrEqualToOne(sliceSize, "sliceSize");

                this.startVersion = startVersion;
                this.sliceSize = sliceSize;
            }

            public StreamSlice<T> Execute()
            {
                return Result(ExecuteQuery(PrepareQuery()));
            }

            public async Task<StreamSlice<T>> ExecuteAsync()
            {
                return Result(await ExecuteQueryAsync(PrepareQuery()));
            }

            StreamSlice<T> Result(ICollection<DynamicTableEntity> entities)
            {
                var streamEntity = FindStreamEntity(entities);
                entities.Remove(streamEntity);

                var stream = BuildStream(streamEntity);
                var events = BuildEvents(entities);

                return new StreamSlice<T>(stream, events, startVersion, sliceSize);
            }

            TableQuery<DynamicTableEntity> PrepareQuery()
            {
                var rowKeyStart = Partition.EventVersionRowKey(startVersion);
                var rowKeyEnd = Partition.EventVersionRowKey(startVersion + sliceSize - 1);

                // ReSharper disable StringCompareToIsCultureSpecific

                var query = Table
                    .CreateQuery<DynamicTableEntity>()
                    .Where(x =>
                           x.PartitionKey == Partition.PartitionKey
                           && (x.RowKey == Partition.StreamRowKey()
                               || (x.RowKey.CompareTo(rowKeyStart)  >= 0
                                   && x.RowKey.CompareTo(rowKeyEnd) <= 0)));

                return (TableQuery<DynamicTableEntity>) query;
            }

            List<DynamicTableEntity> ExecuteQuery(TableQuery<DynamicTableEntity> query)
            {
                var result = new List<DynamicTableEntity>();
                TableContinuationToken token = null;

                do
                {
                    var segment = Table.ExecuteQuerySegmented(query, token);
                    token = segment.ContinuationToken;
                    result.AddRange(segment.Results);
                }
                while (token != null);

                return result;
            }

            async Task<List<DynamicTableEntity>> ExecuteQueryAsync(TableQuery<DynamicTableEntity> query)
            {
                var result = new List<DynamicTableEntity>();
                TableContinuationToken token = null;

                do
                {
                    var segment = await Table.ExecuteQuerySegmentedAsync(query, token).Really();
                    token = segment.ContinuationToken;
                    result.AddRange(segment.Results);
                }
                while (token != null);

                return result;
            }

            DynamicTableEntity FindStreamEntity(IEnumerable<DynamicTableEntity> entities)
            {
                return entities.Single(x => x.RowKey == Partition.StreamRowKey());
            }

            Stream BuildStream(DynamicTableEntity entity)
            {
                return From(Partition, StreamEntity.From(entity));
            }

            static T[] BuildEvents(IEnumerable<DynamicTableEntity> entities)
            {
                return entities.Select(e => e.Properties).Select(properties =>
                {
                    var t = new T();
                    TableEntity.ReadUserObject(t, properties, new OperationContext());
                    return t;
                })
                .ToArray();
            }
        }
    }
}
