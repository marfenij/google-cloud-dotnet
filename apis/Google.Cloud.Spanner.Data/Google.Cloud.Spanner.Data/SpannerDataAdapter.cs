﻿// Copyright 2017 Google Inc. All Rights Reserved.
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

// ReSharper disable UnusedParameter.Local
// ReSharper disable EmptyNamespace
// ReSharper disable MemberCanBePrivate.Global

#if NET45 || NET451
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Data.Common;
using System.Threading;
using Google.Api.Gax;
// ReSharper disable HeuristicUnreachableCode

#endif

namespace Google.Cloud.Spanner.Data
{
#if NET45 || NET451

    /// <summary>
    /// Represents a set of data commands and a database connection that are used to fill the DataSet
    /// and update a Spanner database.
    /// </summary>
    public sealed class SpannerDataAdapter : DbDataAdapter, IDbDataAdapter
    {
        private readonly SpannerParameterCollection _parsedParameterCollection = new SpannerParameterCollection();
        private SpannerCommand _builtInsertCommand;
        private SpannerCommand _builtUpdateCommand;
        private SpannerCommand _builtDeleteCommand;
        private SpannerCommand _builtSelectCommand;
        private string _autoGeneratedCommandTable;

        internal bool AutoCreateCommands => !string.IsNullOrEmpty(AutoGeneratedCommandTable);

        /// <summary>
        /// The <see cref="SpannerCommand"/> used to delete rows.
        /// </summary>
        [Category("Commands")]
        public new SpannerCommand DeleteCommand { get; set; }

        /// <summary>
        /// The <see cref="SpannerCommand"/> used to insert rows.
        /// </summary>
        [Category("Commands")]
        public new SpannerCommand InsertCommand { get; set; }

        /// <summary>
        /// The <see cref="SpannerCommand"/> used to run a SQL Query.
        /// </summary>
        [Category("Commands")]
        public new SpannerCommand SelectCommand { get; set; }

        /// <summary>
        /// The <see cref="SpannerCommand"/> used to update rows.
        /// </summary>
        [Category("Commands")]
        public new SpannerCommand UpdateCommand { get; set; }

        /// <summary>
        /// The connection to the Spanner database.
        /// </summary>
        [Category("Configuration")]
        // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
        public SpannerConnection SpannerConnection { get; set; }

        /// <summary>
        /// The table to use for automatically built commands.
        /// If set, the <see cref="SpannerDataAdapter"/> will automatically create commands for
        /// <see cref="SelectCommand"/>, <see cref="InsertCommand"/>, <see cref="UpdateCommand"/>
        /// and <see cref="DeleteCommand"/> selecting all columns for that Table.
        /// You can choose to customize some or all of the commands and use the autogenerated commands
        /// for ones you do not modify. For example, you can set <see cref="SelectCommand"/> to be
        /// a custom SQL Query, and leave the other commands to be based on <see cref="AutoGeneratedCommandTable"/>
        /// </summary>
        [Category("Configuration")]
        public string AutoGeneratedCommandTable
        {
            get => _autoGeneratedCommandTable;
            set
            {
                // Note that we auto build the commands as a feature of the *Data adapter* versus a separate class.
                // This is done for two reasons:
                // a) We cannot create a "proper" DbCommandBuilder because DDL is not supported by Spanner.
                //    This means any "SpannerCommandBuilder" would not live up to expectations and be castable
                //    to DbCommandBuilder.
                // b) The code for building these commands is both much simpler than other providers and also
                //    somewhat more limiting (it only supports simple table updates whereas other command builders
                //    use TSQL or additional sql commands to inspect a query and determine the proper way to update it).
                //    This means that the auto build feature has more limited use, but is still useful for
                //    quickly getting started and achieving full CRUD on a table with only a single SQL Query + table name.
                //    It also means the code for this support is significantly reduced and does not warrant its own class.
                ClearBuiltCommands();
                _autoGeneratedCommandTable = value;
            }
        }

        /// <summary>
        /// Initializes a new instance of the SpannerDataAdapter class
        /// </summary>
        public SpannerDataAdapter() { }

        /// <summary>
        /// Initializes a new instance of the SpannerDataAdapter class with the specified
        /// </summary>
        /// <param name="connection">A connection to the Spanner database. Must not be null.</param>
        /// <param name="autoGeneratedCommandTable">The Spanner database table to use for automatically generated commands.
        /// May be null.</param>
        /// <param name="primaryKeys">The set of columns that form the primary key for <paramref name="autoGeneratedCommandTable"/>.</param>
        public SpannerDataAdapter(SpannerConnection connection, string autoGeneratedCommandTable, params string[] primaryKeys)
        {
            GaxPreconditions.CheckNotNull(connection, nameof(connection));
            SpannerConnection = connection;
            AutoGeneratedCommandTable = autoGeneratedCommandTable;
            if (primaryKeys != null)
            {
                AutoGeneratedCommandPrimaryKeys.UnionWith(primaryKeys);
            }
        }

        [Browsable(false)]
        IDbCommand IDbDataAdapter.DeleteCommand
        {
            get => DeleteCommand ?? GetBuiltDeleteCommand();
            set => DeleteCommand = (SpannerCommand) value;
        }

        [Browsable(false)]
        IDbCommand IDbDataAdapter.InsertCommand
        {
            get => InsertCommand ?? GetBuiltInsertCommand();
            set => InsertCommand = (SpannerCommand) value;
        }

        [Browsable(false)]
        IDbCommand IDbDataAdapter.SelectCommand
        {
            get => SelectCommand ?? GetBuiltSelectCommand();
            set => SelectCommand = (SpannerCommand) value;
        }

        [Browsable(false)]
        IDbCommand IDbDataAdapter.UpdateCommand
        {
            get => UpdateCommand ?? GetBuiltUpdateCommand();
            set => UpdateCommand = (SpannerCommand) value;
        }

        /// <summary>
        /// The set of primary keys defined for <see cref="AutoGeneratedCommandTable"/>.
        /// </summary>
        public HashSet<string> AutoGeneratedCommandPrimaryKeys { get; } = new HashSet<string>();

        /// <summary>
        /// Occurs during Update after a command is executed against the data source.
        /// </summary>
        public event EventHandler<SpannerRowUpdatedEventArgs> RowUpdated;

        /// <summary>
        /// Occurs during Update before a command is executed against the data source.
        /// </summary>
        public event EventHandler<SpannerRowUpdatingEventArgs> RowUpdating;

        /*
         * Implement abstract methods inherited from DbDataAdapter.
         */
        /// <inheritdoc />
        protected override RowUpdatedEventArgs CreateRowUpdatedEvent(
            DataRow dataRow,
            IDbCommand command,
            StatementType statementType,
            DataTableMapping tableMapping) => new SpannerRowUpdatedEventArgs(
            dataRow, command, statementType, tableMapping);

        /// <inheritdoc />
        protected override RowUpdatingEventArgs CreateRowUpdatingEvent(
        DataRow dataRow,
        IDbCommand command,
            StatementType statementType,
            DataTableMapping tableMapping) => new SpannerRowUpdatingEventArgs(
            dataRow, command, statementType, tableMapping);

        /// <inheritdoc />
        protected override void OnRowUpdated(RowUpdatedEventArgs rowUpdatedEventArgs)
        {
            RowUpdated?.Invoke(this, (SpannerRowUpdatedEventArgs) rowUpdatedEventArgs);
        }

        /// <inheritdoc />
        protected override int Fill(
            DataSet dataSet,
            int startRecord,
            int maxRecords,
            string srcTable,
            IDbCommand command,
            CommandBehavior behavior)
        {
            // ReSharper disable once ConditionIsAlwaysTrueOrFalse
            if (command == null)
            {
                command = GetBuiltSelectCommand();
            }
            return base.Fill(dataSet, startRecord, maxRecords, srcTable, command, behavior);
        }

        /// <inheritdoc />
        protected override int Fill(
            DataSet dataSet,
            string srcTable,
            IDataReader dataReader,
            int startRecord,
            int maxRecords)
        {
            var spannerDataReader = dataReader as SpannerDataReader;
            if (spannerDataReader != null && AutoCreateCommands)
            {
                var readerMetadata =
                    spannerDataReader.PopulateMetadataAsync(CancellationToken.None).ResultWithUnwrappedExceptions();
                foreach (var field in readerMetadata.RowType.Fields)
                {
                    _parsedParameterCollection.Add(
                        new SpannerParameter(field.Name, SpannerDbType.FromProtobufType(field.Type), null, field.Name));
                }
            }

            return base.Fill(dataSet, srcTable, dataReader, startRecord, maxRecords);
        }

        private void ClearBuiltCommands()
        {
            _builtInsertCommand = null;
            _builtDeleteCommand = null;
            _builtUpdateCommand = null;
        }

        private SpannerCommand GetBuiltInsertCommand()
        {
            if (_builtInsertCommand == null && _parsedParameterCollection != null && AutoCreateCommands)
            {
                _builtInsertCommand =
                    SpannerConnection.CreateInsertCommand(AutoGeneratedCommandTable, _parsedParameterCollection);
            }
            return _builtInsertCommand;
        }

        private SpannerCommand GetBuiltUpdateCommand()
        {
            if (_builtUpdateCommand == null && _parsedParameterCollection != null && AutoCreateCommands)
            {
                _builtUpdateCommand =
                    SpannerConnection.CreateUpdateCommand(AutoGeneratedCommandTable, _parsedParameterCollection);
            }
            return _builtUpdateCommand;
        }

        private SpannerCommand GetBuiltDeleteCommand()
        {
            if (_builtDeleteCommand == null && _parsedParameterCollection != null && AutoCreateCommands)
            {
                SpannerParameterCollection deleteCollection = new SpannerParameterCollection();
                //Filter the delete collection down to just the ones representing the primary keys.
                foreach(SpannerParameter parameter in _parsedParameterCollection)
                {
                    if (AutoGeneratedCommandPrimaryKeys.Contains(parameter.ParameterName))
                    {
                        deleteCollection.Add(parameter);
                    }
                }
                _builtDeleteCommand =
                    SpannerConnection.CreateDeleteCommand(AutoGeneratedCommandTable, deleteCollection);
            }
            return _builtDeleteCommand;
        }

        private SpannerCommand GetBuiltSelectCommand()
        {
            if (_builtSelectCommand == null && AutoCreateCommands)
            {
                _builtSelectCommand = SpannerConnection.CreateSelectCommand($"SELECT * FROM {AutoGeneratedCommandTable}");
            }
            return _builtSelectCommand;
        }

        /// <inheritdoc />
        protected override void OnRowUpdating(RowUpdatingEventArgs rowUpdatingEventArgs)
        {
            RowUpdating?.Invoke(this, (SpannerRowUpdatingEventArgs) rowUpdatingEventArgs);
        }
    }
#endif
}
