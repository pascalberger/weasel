using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Baseline;
using Npgsql;

namespace Weasel.Postgresql
{
    [Obsolete("Replace w/ new SchemaMigration")]
    public class SchemaPatch
    {
        private readonly Action<NpgsqlCommand, Exception> _exceptionHandler;
        public DdlRules Rules { get; }

        public static string ToDropFileName(string updateFile)
        {
            var containingFolder = updateFile.ParentDirectory();
            var rawFileName = Path.GetFileNameWithoutExtension(updateFile);
            var ext = Path.GetExtension(updateFile);

            var dropFile = $"{rawFileName}.drop{ext}";

            return containingFolder.IsEmpty() ? dropFile : containingFolder.AppendPath(dropFile);
        }

        private readonly DDLRecorder _up = new DDLRecorder();
        private readonly DDLRecorder _down = new DDLRecorder();
        private readonly IDDLRunner _liveRunner;

        private static readonly Action<NpgsqlCommand, Exception> _defaultExceptionHandler = (c, e) => ExceptionDispatchInfo.Capture(e).Throw();

        public SchemaPatch(DdlRules rules, Action<NpgsqlCommand, Exception> exceptionHandler = null)
        {
            _exceptionHandler = exceptionHandler ?? _defaultExceptionHandler;
            Rules = rules;
        }

        public SchemaPatch(DdlRules rules, StringWriter upWriter, Action<NpgsqlCommand, Exception> exceptionHandler = null) : this(rules, new DDLRecorder(upWriter), exceptionHandler)
        {
        }

        public SchemaPatch(DdlRules rules, IDDLRunner liveRunner, Action<NpgsqlCommand, Exception> exceptionHandler = null) : this(rules, exceptionHandler)
        {
            _liveRunner = liveRunner;
        }

        public StringWriter DownWriter => _down.Writer;

        public StringWriter UpWriter => _up.Writer;

        public IDDLRunner Updates => _liveRunner ?? _up;
        public IDDLRunner Rollbacks => _down;

        public string UpdateDDL => _up.Writer.ToString();
        public string RollbackDDL => _down.Writer.ToString();

        public SchemaPatchDifference Difference
        {
            get
            {
                if (!Migrations.Any())
                    return SchemaPatchDifference.None;

                if (Migrations.Any(x => x.Difference == SchemaPatchDifference.Invalid))
                {
                    return SchemaPatchDifference.Invalid;
                }

                if (Migrations.Any(x => x.Difference == SchemaPatchDifference.Update))
                {
                    return SchemaPatchDifference.Update;
                }

                if (Migrations.Any(x => x.Difference == SchemaPatchDifference.Create))
                {
                    return SchemaPatchDifference.Create;
                }

                return SchemaPatchDifference.None;
            }
        }

        public void WriteScript(TextWriter writer, Action<TextWriter> writeStep, bool transactionalScript = true)
        {
            if (transactionalScript)
            {
                writer.WriteLine("DO LANGUAGE plpgsql $tran$");
                writer.WriteLine("BEGIN");
                writer.WriteLine("");
            }

            if (Rules.Role.IsNotEmpty())
            {
                writer.WriteLine($"SET ROLE {Rules.Role};");
                writer.WriteLine("");
            }

            writeStep(writer);

            if (Rules.Role.IsNotEmpty())
            {
                writer.WriteLine($"RESET ROLE;");
                writer.WriteLine("");
            }

            if (transactionalScript)
            {
                writer.WriteLine("");
                writer.WriteLine("END;");
                writer.WriteLine("$tran$;");
            }
        }

        public void WriteFile(string file, string sql, bool transactionalScript)
        {
            using (var stream = new FileStream(file, FileMode.Create))
            {
                var writer = new StreamWriter(stream) { AutoFlush = true };

                WriteScript(writer, w => w.WriteLine(sql), transactionalScript);

                stream.Flush(true);
            }
        }

        public void WriteUpdateFile(string file, bool transactionalScript = true)
        {
            WriteFile(file, UpdateDDL, transactionalScript);
        }

        public void WriteRollbackFile(string file, bool transactionalScript = true)
        {
            WriteFile(file, RollbackDDL, transactionalScript);
        }

        public readonly IList<ISchemaObjectDelta> Migrations = new List<ISchemaObjectDelta>();

        public void Log(ISchemaObject schemaObject, SchemaPatchDifference difference)
        {
            var migration = new SchemaObjectDelta(schemaObject, difference);
            Migrations.Add(migration);
        }

        public void AssertPatchingIsValid(AutoCreate autoCreate)
        {
            if (autoCreate == AutoCreate.All)
                return;

            var difference = Difference;

            if (difference == SchemaPatchDifference.None)
                return;

            if (difference == SchemaPatchDifference.Invalid)
            {
                var invalidObjects = Migrations.Where(x => x.Difference == SchemaPatchDifference.Invalid).Select(x => x.SchemaObject.Identifier.ToString()).Join(", ");
                throw new InvalidOperationException($"Marten cannot derive updates for objects {invalidObjects}");
            }

            if (difference == SchemaPatchDifference.Update && autoCreate == AutoCreate.CreateOnly)
            {
                var updates = Migrations.Where(x => x.Difference == SchemaPatchDifference.Update).ToArray();
                if (updates.Any())
                {
                    throw new InvalidOperationException($"Marten cannot apply updates in CreateOnly mode to existing items {updates.Select(x => x.SchemaObject.Identifier.QualifiedName).Join(", ")}");
                }
            }
        }

        public async Task Apply(NpgsqlConnection conn, AutoCreate autoCreate, ISchemaObject[] schemaObjects)
        {
            if (!schemaObjects.Any())
                return;

            // Let folks just fail if anything is wrong.
            // Per https://github.com/JasperFx/marten/issues/711
            if (autoCreate == AutoCreate.None)
                return;

            var cmd = conn.CreateCommand();
            var builder = new CommandBuilder(cmd);

            foreach (var schemaObject in schemaObjects)
            {
                schemaObject.ConfigureQueryCommand(builder);
            }

            cmd.CommandText = builder.ToString();

            try
            {
                using var reader = await cmd.ExecuteReaderAsync();
                await apply(schemaObjects[0], autoCreate, reader);
                for (var i = 1; i < schemaObjects.Length; i++)
                {
                    await reader.NextResultAsync();
                    await apply(schemaObjects[i], autoCreate, reader);
                }
            }
            catch (Exception e)
            {
                _exceptionHandler(cmd, e);
            }

            AssertPatchingIsValid(autoCreate);
        }

        private async Task apply(ISchemaObject schemaObject, AutoCreate autoCreate, DbDataReader reader)
        {
            var delta = await schemaObject.CreateDelta(reader);

            // TODO -- does anything need to happen here if there's no delta?
            
            // Cleaner if the answer is no
            Migrations.Add(delta);
        }

        public Task Apply(NpgsqlConnection connection, AutoCreate autoCreate, ISchemaObject schemaObject)
        {
            return Apply(connection, autoCreate, new ISchemaObject[] { schemaObject });
        }
    }
    
    public static class DDLRunnerExtensions
    {
        public static void Drop(this IDDLRunner runner, object subject, DbObjectName table)
        {
            var sql = $"drop table if exists {table.QualifiedName} cascade;";
            runner.Apply(subject, sql);
        }

        public static void RemoveColumn(this IDDLRunner runner, object subject, DbObjectName table, string columnName)
        {
            var sql = $"alter table if exists {table.QualifiedName} drop column if exists {columnName};";

            runner.Apply(subject, sql);
        }
    }


}
