using Microsoft.Data.SqlClient;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FFI_Exam_Monitoring_Service
{
    public class DBQuery
    {
        private string dbConnectionSQL = $"User ID={AppConfig.SQLDatabaseSettings.UserID}; " +
                                 $"Password={AppConfig.SQLDatabaseSettings.Password}; " +
                                 $"Server={AppConfig.SQLDatabaseSettings.Server}; " +
                                 $"Database={AppConfig.SQLDatabaseSettings.Database}; " +
                                 $"Encrypt={AppConfig.SQLDatabaseSettings.Encrypt};";

        // Loggers
        private static NLog.Logger fileLogger = NLog.LogManager.GetLogger("FileLog");
        private static NLog.Logger consoleLogger = NLog.LogManager.GetLogger("DCMLog");


        public DataTable GetExamData()
        {
            DataTable dt = new DataTable();

            try
            {
                using (SqlConnection con = new SqlConnection(dbConnectionSQL))
                {
                    string sql = "SELECT e.AccessionNumber, e.PlacerGroupNumber, ec.Description " +
                                 "FROM ClinicalDataStore.Exam.Exam e " +
                                 "Inner Join ClinicalDataStore.Exam.ExamCode ec on e.ExamCodeId = ec.ExamCodeId " +
                                 "WHERE e.ObservationDateTime >= DATEADD(hour, -" + AppConfig.GeneralSettings.HoursToCheck + ", GETDATE()) " +
                                 "AND e.ObservationDateTime <= GETDATE() + 1 " +
                                 "AND e.ExamStateTypeCode != @ExamStateTypeCode;";

                    using (SqlCommand cmd = new SqlCommand(sql, con))
                    {
                        con.Open();
                        cmd.Parameters.Add(new SqlParameter("ExamStateTypeCode", "X"));

                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {                            
                            dt.Load(reader);
                        }
                    }
                }
            }

            catch (Exception ex)
            {
                consoleLogger.Error($"({AppConfig.SQLDatabaseSettings.Server}) The following exception occurred while retrieving the exam data FFI Clinical Data Store: ");
                consoleLogger.Error(ex.ToString());

                fileLogger.Error($"({AppConfig.SQLDatabaseSettings.Server}) The following exception occurred while retrieving the exam data FFI Clinical Data Store: ");
                fileLogger.Error(ex.ToString());
            }

            return dt;
        }        

        public async Task UpdateSitesAsync(DataTable examTable)
        {
            var remoteConnectionList = AppConfig.RemoteConnections;
            var tasks = new List<Task>();

            int maxConcurrentConnections = 10;
            var insertSemaphore = new SemaphoreSlim(maxConcurrentConnections);

            foreach (RemoteConnection remoteConnection in remoteConnectionList.Where(rc => Convert.ToBoolean(rc.Active)))
            {
                tasks.Add(Task.Run(async () =>
                {
                    var dbConnectionPostgres = $"Server={remoteConnection.Server}; Port={remoteConnection.Port}; User ID={remoteConnection.UserID}; Password={remoteConnection.Password}; Database={remoteConnection.Database}; Timeout={remoteConnection.Timeout}";

                    // Mark as Do Not Read
                    await UpdateStatusToDoNotRead(dbConnectionPostgres, remoteConnection, examTable);

                    // Updating to "Ready"
                    await UpdateStatusToReady(dbConnectionPostgres, remoteConnection, examTable);

                    // After "Ready" update completes, start "Not Ready" update
                    await UpdateStatusToNotReady(dbConnectionPostgres, remoteConnection);

                    // Update the FFI Status table in HL7_Custom
                    var dt = await GetDataFromSite(remoteConnection, dbConnectionPostgres);

                    if (dt.Rows.Count > 0)
                    {
                        var insertTasks = new List<Task>();

                        foreach(DataRow row in dt.Rows)
                        {
                            await insertSemaphore.WaitAsync();

                            insertTasks.Add(Task.Run(async () =>
                            {
                                try
                                {
                                    var studyDate = new DateTime(1900, 1, 1);
                                    var birthDate = new DateTime(1900, 1, 1);

                                    StudyData studyData = new StudyData();

                                    studyData.source = remoteConnection.Name;
                                    studyData.patient_id = row.ItemArray[0].ToString();
                                    studyData.accession = row.ItemArray[1].ToString();
                                    studyData.facility = row.ItemArray[2].ToString();
                                    studyData.patient_group = row.ItemArray[3].ToString();
                                    studyData.modality = row.ItemArray[4].ToString();
                                    studyData.anatomical_area = row.ItemArray[5].ToString();
                                    studyData.patient_last_name = row.ItemArray[6].ToString();
                                    studyData.patient_first_name = row.ItemArray[7].ToString();

                                    DateTime.TryParse(row.ItemArray[8].ToString(), out birthDate);
                                    studyData.birth_date = birthDate;

                                    studyData.custom_3 = row.ItemArray[9].ToString();

                                    DateTime.TryParse(row.ItemArray[10].ToString(), out studyDate);
                                    studyData.study_date = studyDate;

                                    studyData.status = row.ItemArray[11].ToString();

                                    var insertCount = await InsertStatusData(remoteConnection, studyData);
                                }

                                finally
                                {
                                    insertSemaphore.Release();
                                }
                            }));                            
                        }

                        await Task.WhenAll(insertTasks);
                    }
                }));
            }

            await Task.WhenAll(tasks);
        }

        private async Task UpdateStatusToReady(string dbConnectionPostgres, RemoteConnection remoteConnection, DataTable examTable)
        {
            int readyCount = 0;
            var maxConncurrentTasks = 10;
            var throttler = new SemaphoreSlim(maxConncurrentTasks);

            List<Task<bool>> updateTasks = new List<Task<bool>>();

            consoleLogger.Info($"Updating Studies to \"Ready\" status for the facility \"{remoteConnection.Name}\"...");
            fileLogger.Info($"Updating Studies to \"Ready\" status for the facility \"{remoteConnection.Name}\"...");

            foreach(DataRow row in examTable.Rows)
            {
                updateTasks.Add(UpdateRowStatus(row, dbConnectionPostgres, remoteConnection, throttler));
            }

            var results = await Task.WhenAll(updateTasks);
            readyCount = results.Count(r => r);

            consoleLogger.Info($"{readyCount} studies were set to a status of \"Ready\" at the \"{remoteConnection.Name}\" patient group.");
            fileLogger.Info($"{readyCount} studies were set to a status of \"Ready\" at the \"{remoteConnection.Name}\" patient group.");
        }

        private async Task<bool> UpdateRowStatus(DataRow row, string dbConnectionPostgres, RemoteConnection remoteConnection, SemaphoreSlim throttler)
        {
            await throttler.WaitAsync();

            try
            {
                return await UpdateStudyCustomField(row["AccessionNumber"].ToString(), row["PlacerGroupNumber"].ToString(), dbConnectionPostgres, remoteConnection.Server, remoteConnection.MarkIfVerified);
            }

            catch (Exception ex)
            {
                consoleLogger.Error($"({remoteConnection.Name}) Error updating study for {row["AccessionNumber"]}: {ex.ToString()}");
                fileLogger.Error($"({remoteConnection.Name}) Error updating study for {row["AccessionNumber"]}: {ex.ToString()}");
                return false;
            }

            finally
            {
                throttler.Release();
            }
        }

        private async Task UpdateStatusToNotReady(string dbConnectionPostgres, RemoteConnection remoteConnection)
        {
            consoleLogger.Info($"Updating Studies to a status of \"Not Ready\" at the facility \"{remoteConnection.Name}\"...");
            fileLogger.Info($"Updating Studies to a status of \"Not Ready\" at the facility \"{remoteConnection.Name}\"...");

            int updateCount = await UpdateStudyCustomField(dbConnectionPostgres, remoteConnection.Server);

            consoleLogger.Info($"{updateCount} studies were set to a status of \"Not Ready\" at the \"{remoteConnection.Name}\" patient group.");
            fileLogger.Info($"{updateCount} studies were set to a status of \"Not Ready\" at the \"{remoteConnection.Name}\" patient group.");
        }

        private async Task<bool> UpdateStudyCustomField(string accession, string patientGroup, string connectionString, string siteName, string markIfVerified)
        {
            var wasUpdated = false;

            bool mark_if_verified = false;
            bool.TryParse(markIfVerified, out mark_if_verified);

            try
            {
                using (NpgsqlConnection con = new NpgsqlConnection(connectionString))
                {
                    await con.OpenAsync();

                    var query = string.Empty;

                    if (mark_if_verified)
                    {
                        query = "update " +
                                "pacs.studies st " +
                                "set custom_3 = 'ready' " +
                                "from " +
                                "pacs.patients pa " +
                                "where " +
                                "st.patient = pa.id " +
                                "and st.study_date >= now() - interval '" + AppConfig.GeneralSettings.HoursToCheck + " hours'" +
                                "and st.accession = $1 " +
                                "and pa.patient_group = $2 " +
                                "and st.status = $3 " +
                                "and coalesce(st.custom_3, '') != $4 " +
                                "RETURNING st.id, st.custom_3;";

                        using (NpgsqlCommand cmd = new NpgsqlCommand(query, con))
                        {
                            cmd.Parameters.Add(new NpgsqlParameter { Value = accession });
                            cmd.Parameters.Add(new NpgsqlParameter { Value = patientGroup });
                            cmd.Parameters.Add(new NpgsqlParameter { Value = 3 });
                            cmd.Parameters.Add(new NpgsqlParameter { Value = "DO NOT READ" });

                            int rowsAffected = await cmd.ExecuteNonQueryAsync();

                            if (rowsAffected > 0)
                                wasUpdated = true;
                        }
                    }

                    else
                    {
                        query = "update " +
                                "pacs.studies st " +
                                "set custom_3 = 'ready' " +
                                "from " +
                                "pacs.patients pa " +
                                "where " +
                                "st.patient = pa.id " +
                                "and st.study_date >= now() - interval '" + AppConfig.GeneralSettings.HoursToCheck + " hours'" +
                                "and st.accession = $1 " +
                                "and pa.patient_group = $2 " +
                                "and coalesce(st.custom_3, '') != $3 " +
                                "RETURNING st.id, st.custom_3;";

                        using (NpgsqlCommand cmd = new NpgsqlCommand(query, con))
                        {
                            cmd.Parameters.Add(new NpgsqlParameter { Value = accession });
                            cmd.Parameters.Add(new NpgsqlParameter { Value = patientGroup });
                            cmd.Parameters.Add(new NpgsqlParameter { Value = "DO NOT READ" });

                            int rowsAffected = await cmd.ExecuteNonQueryAsync();

                            if (rowsAffected > 0)
                                wasUpdated = true;
                        }
                    } 
                }
            }
            catch (Exception ex)
            {
                consoleLogger.Error($"({siteName}) The following exception occurred while updating the custom_3 field to \"Ready\" in the NovaPACS studies table: ");
                consoleLogger.Error(ex.ToString());

                fileLogger.Error($"({siteName}) The following exception occurred while updating the custom_3 field to \"Ready\" in the NovaPACS studies table: ");
                fileLogger.Error(ex.ToString());
            }

            return wasUpdated;
        }

        private async Task<int> UpdateStudyCustomField(string connectionString, string siteName)
        {
            int updateCount = 0;
            try
            {
                var query = string.Empty;

                query = "update " +
                            "pacs.studies st " +
                            "set custom_3 = 'not ready' " +
                            "from " +
                            "pacs.patients pa " +
                            "where " +
                            "st.patient = pa.id " +
                            "and st.study_date >= now() - interval '" + AppConfig.GeneralSettings.HoursToCheck + " hours'" +
                            "and coalesce(st.custom_3, '') != 'ready' " +
                            "and coalesce(st.custom_3, '') != 'DO NOT READ'; ";

                using (NpgsqlConnection con = new NpgsqlConnection(connectionString))
                {
                    await con.OpenAsync();

                    using (NpgsqlCommand command = new NpgsqlCommand(query, con))
                    {
                        updateCount = await command.ExecuteNonQueryAsync();
                    }
                }
            }

            catch (Exception ex)
            {
                consoleLogger.Error($"({siteName}) The following exception occurred while marking custom_3 field \"Not Ready\" in the NovaPACS studies table: ");
                consoleLogger.Error(ex.ToString());

                fileLogger.Error($"({siteName}) The following exception occurred while marking custom_3 field \"Not Ready\" in the NovaPACS studies table: ");
                fileLogger.Error(ex.ToString());
            }

            return updateCount;
        }

        private async Task UpdateStatusToDoNotRead(string dbConnectionPostgres, RemoteConnection remoteConnection, DataTable examTable)
        {
            var maxConncurrentTasks = 10;
            var throttler = new SemaphoreSlim(maxConncurrentTasks);

            List<Task<bool>> updateTasks = new List<Task<bool>>();

            if (AppConfig.Exclusions.TryGetValue(remoteConnection.Name, out var exclusionString))
            {
                var studyDescriptions = exclusionString.Split('|', StringSplitOptions.RemoveEmptyEntries);

                foreach (DataRow row in examTable.Rows)
                {
                    var description = row["Description"].ToString();

                    if (studyDescriptions.Any(sd => sd.Equals(description, StringComparison.OrdinalIgnoreCase)))
                    {
                        updateTasks.Add(DNRUpdateRowStatus(
                            row["AccessionNumber"].ToString(),
                            row["PlacerGroupNumber"].ToString(),
                            dbConnectionPostgres,
                            remoteConnection,
                            throttler
                        ));
                    }
                }
            }

            consoleLogger.Info($"({remoteConnection.Name}) Updating the Custom_3 field to \"DO NOT READ\" for excluded study descriptions...");
            fileLogger.Info($"({remoteConnection.Name}) Updating the Custom_3 field to \"DO NOT READ\" for excluded study descriptions...");

            var results = await Task.WhenAll(updateTasks);
            int dnrCount = results.Count(r => r);

            consoleLogger.Info($"({remoteConnection.Name}) {dnrCount} custom_3 fields were set to \"DO NOT READ\"");
            fileLogger.Info($"({remoteConnection.Name}) {dnrCount} custom_3 fields were set to \"DO NOT READ\"");
        }

        public async Task<bool> DNRUpdateRowStatus(string accession, string placerGroupNumber, string dbConnectionPostgres, RemoteConnection remoteConnection, SemaphoreSlim throttler)
        {
            await throttler.WaitAsync();

            try
            {
                return await SetAsDoNotRead(accession, placerGroupNumber, dbConnectionPostgres, remoteConnection);
            }

            catch (Exception ex)
            {
                consoleLogger.Error($"({remoteConnection.Name}) Error updating study for accession number \"{accession}\": {ex.ToString()}");
                fileLogger.Error($"({remoteConnection.Name}) Error updating study for accession number \"{accession}\": {ex.ToString()}");
                return false;
            }

            finally
            {
                throttler.Release();
            }
        }

        public async Task<bool> SetAsDoNotRead(string accession, string patientGroup, string connectionString, RemoteConnection remoteConnnection)
        {
            bool wasSet = false;

            var query = "update " +
                        "pacs.studies st " +
                        "set custom_3 = 'DO NOT READ' " +
                        "from " +
                        "pacs.patients pa " +
                        "where " +
                        "st.patient = pa.id " +
                        "and st.study_date >= now() - interval '" + AppConfig.GeneralSettings.HoursToCheck + " hours'" +
                        "and st.accession = $1 " +
                        "and pa.patient_group = $2 " +
                        "RETURNING st.id, st.custom_3;";

            try
            {
                using (NpgsqlConnection con = new NpgsqlConnection(connectionString))
                {
                    await con.OpenAsync();

                    using (NpgsqlCommand cmd = new NpgsqlCommand(query, con))
                    {
                        cmd.Parameters.Add(new NpgsqlParameter { Value = accession });
                        cmd.Parameters.Add(new NpgsqlParameter { Value = patientGroup });

                        int setCount = await cmd.ExecuteNonQueryAsync();

                        if (setCount > 0)
                        {
                            wasSet = true;
                        }
                    }
                }
            }

            catch (Exception ex)
            {
                consoleLogger.Error($"({remoteConnnection.Name}) The following exception occurred while setting studies to \"DO NOT READ\": ");
                consoleLogger.Error(ex.ToString());

                fileLogger.Error($"({remoteConnnection.Name}) The following exception occurred while setting studies to \"DO NOT READ\": ");
                fileLogger.Error(ex.ToString());
            }

            return wasSet;
        }

        public async Task<DataTable> GetDataFromSite(RemoteConnection remoteConnection, string connectionString)
        {
            DataTable dt = new DataTable();
            dt.Columns.Add("patient_id");
            dt.Columns.Add("accession");
            dt.Columns.Add("facility");
            dt.Columns.Add("patient_group");
            dt.Columns.Add("modality");
            dt.Columns.Add("anatomical_area");
            dt.Columns.Add("patient_last_name");
            dt.Columns.Add("patient_first_name");
            dt.Columns.Add("birth_date");
            dt.Columns.Add("custom_3");
            dt.Columns.Add("study_date");
            dt.Columns.Add("status");


            try
            {
                NpgsqlDataReader statusReader;
                var query = @"select " +                               
                               "pa.patient_id, " +
                               "st.accession, " +
                               "fa.name as facility, " +
                               "pa.patient_group, " +
                               "st.modality, " +
                               "st.anatomical_area, " +
                               "pa.last_name as patient_last_name, " +
                               "pa.first_name as patient_first_name, " +
                               "pa.birth_time as birth_date, " +
                               "st.custom_3, " +
                               "st.study_date, " +
                               "cast('not ready' as text) as status " +
                              "from " +
                              "pacs.studies st " +
                              "inner join " +
                                "pacs.patients pa on st.patient = pa.id " +
                              "inner join " +
                                "shared.facilities fa on st.facility_id = fa.facility_id " +
                              "where " +
                                "st.study_date >= now() - interval '" + AppConfig.GeneralSettings.HoursToCheck + " hours' " +
                                "and st.study_date <= now() " +
                               " and coalesce(st.custom_3, '') != @ReadyStatus;";

                using (NpgsqlConnection con = new NpgsqlConnection(connectionString))
                {
                    await con.OpenAsync();

                    using (NpgsqlCommand cmd = new NpgsqlCommand(query, con))
                    {
                        cmd.CommandTimeout = 600;
                        cmd.Parameters.Add(new NpgsqlParameter("ReadyStatus", "ready"));

                        statusReader = await cmd.ExecuteReaderAsync();

                        while(await statusReader.ReadAsync())
                        {
                            dt.Rows.Add(statusReader["patient_id"],
                                        statusReader["accession"],
                                        statusReader["facility"],
                                        statusReader["patient_group"],
                                        statusReader["modality"],
                                        statusReader["anatomical_area"],
                                        statusReader["patient_last_name"],
                                        statusReader["patient_first_name"],
                                        statusReader["birth_date"],
                                        statusReader["custom_3"],
                                        statusReader["study_date"],
                                        statusReader["status"]);
                        }
                    }
                }
            }

            catch (Exception ex)
            {
                consoleLogger.Error($"({remoteConnection.Name}) The following exception occurred while retrieving data for studies with a status of \"Not Ready\" in NovaPACS: ");
                consoleLogger.Error(ex.ToString());

                fileLogger.Error($"({remoteConnection.Name}) The following exception occurred while retrieving data for studies with a status of \"Not Ready\" in NovaPACS: ");
                fileLogger.Error(ex.ToString());
            }

            return dt;
        }

        public async Task<int> InsertStatusData(RemoteConnection remoteConnection, StudyData studyData)
        {
            string connectionString = $"Server={AppConfig.StatusTrackerSettings.Server}; " +
                                                         $"Port={AppConfig.StatusTrackerSettings.Port}; " +
                                                         $"User ID={AppConfig.StatusTrackerSettings.UserID};" +
                                                         $"Password={AppConfig.StatusTrackerSettings.Password}; " +
                                                         $"Database={AppConfig.StatusTrackerSettings.Database}; " +
                                                         $"Timeout={AppConfig.StatusTrackerSettings.Timeout}";

            int insertCount = 0;
            DateTime defaultDate = new DateTime(1900, 1, 1);

            try
            {
                var query = "insert into public.ffi_status (source, patient_id, accession, facility, patient_group, modality, anatomical_area, patient_last_name, patient_first_name, birth_date, custom_3, study_date, status) " +
                            "values (@source, @patient_id, @accession, @facility, @patient_group, @modality, @anatomical_area, @patient_last_name, @patient_first_name, @birth_date, @custom_3, @study_date, @status) " +
                            "ON CONFLICT (accession, patient_group) DO UPDATE SET " +
                            "source = EXCLUDED.source, patient_id = EXCLUDED.patient_id, facility = EXCLUDED.facility, " +
                            "modality = EXCLUDED.modality, anatomical_area = EXCLUDED.anatomical_area, " +
                            "patient_last_name = EXCLUDED.patient_last_name, patient_first_name = EXCLUDED.patient_first_name, " +
                            "birth_date = EXCLUDED.birth_date, custom_3 = EXCLUDED.custom_3, " +
                            "study_date = EXCLUDED.study_date, status = EXCLUDED.status";

                using (NpgsqlConnection con = new NpgsqlConnection(connectionString))
                {
                    await con.OpenAsync();

                    using(NpgsqlCommand cmd = new NpgsqlCommand(query, con))
                    {
                        cmd.CommandTimeout = 600;

                        cmd.Parameters.Add(new NpgsqlParameter("source", studyData.source));
                        cmd.Parameters.Add(new NpgsqlParameter("patient_id", studyData.patient_id));
                        cmd.Parameters.Add(new NpgsqlParameter("accession", studyData.accession));
                        cmd.Parameters.Add(new NpgsqlParameter("facility", studyData.facility));
                        cmd.Parameters.Add(new NpgsqlParameter("patient_group", studyData.patient_group));
                        cmd.Parameters.Add(new NpgsqlParameter("modality", studyData.modality));
                        cmd.Parameters.Add(new NpgsqlParameter("anatomical_area", studyData.anatomical_area));
                        cmd.Parameters.Add(new NpgsqlParameter("patient_last_name", studyData.patient_last_name));
                        cmd.Parameters.Add(new NpgsqlParameter("patient_first_name", studyData.patient_first_name));
                        cmd.Parameters.Add(new NpgsqlParameter("custom_3", studyData.custom_3));
                        cmd.Parameters.Add(new NpgsqlParameter("status", studyData.status));                                              
                        cmd.Parameters.Add(new NpgsqlParameter("birth_date", studyData.birth_date));
                        cmd.Parameters.Add(new NpgsqlParameter("study_date", studyData.study_date));

                        insertCount = await cmd.ExecuteNonQueryAsync();
                    }
                }
            }

            catch (Exception ex)
            {
                consoleLogger.Error($"({remoteConnection.Name}) The following exception occurred while inserting study data with a status of \"Not Ready\" into HL7_Custom: ");
                consoleLogger.Error(ex.ToString());

                fileLogger.Error($"({remoteConnection.Name}) The following exception occurred while inserting study data with a status of \"Not Ready\" into HL7_Custom: ");
                fileLogger.Error(ex.ToString());
            }

            return insertCount;
        }

        public async Task RemoveStudiesFromStatusTracker()
        {

            string connectionString = $"Server={AppConfig.StatusTrackerSettings.Server}; " +
                                                         $"Port={AppConfig.StatusTrackerSettings.Port}; " +
                                                         $"User ID={AppConfig.StatusTrackerSettings.UserID};" +
                                                         $"Password={AppConfig.StatusTrackerSettings.Password}; " +
                                                         $"Database={AppConfig.StatusTrackerSettings.Database}; " +
                                                         $"Timeout={AppConfig.StatusTrackerSettings.Timeout}";
            try
            {
                var query = "delete " +
                            "from " +
                            "public.ffi_status " +
                            "where " +
                            "study_date<now() - interval '" + AppConfig.StatusTrackerSettings.StudyRetentionInDays  + " days';";

                using (NpgsqlConnection con = new NpgsqlConnection(connectionString))
                {
                    await con.OpenAsync();

                    using (NpgsqlCommand cmd = new NpgsqlCommand(query, con))
                    {
                        var delCount = await cmd.ExecuteNonQueryAsync();

                        if (delCount > 0)
                        {
                            consoleLogger.Info($"{delCount.ToString("N0")} studies older than {AppConfig.StatusTrackerSettings.StudyRetentionInDays} days old were removed from the status tracker." );
                            fileLogger.Info($"{delCount.ToString("N0")} studies older than {AppConfig.StatusTrackerSettings.StudyRetentionInDays} days old were removed from the status tracker.");
                        }
                    }
                }
            }

            catch (Exception ex)
            {
                consoleLogger.Error($"The following exception occurred while removing studies from the Status Tracker: ");
                consoleLogger.Error(ex.ToString());

                fileLogger.Error($"The following exception occurred while removing studies from the Status Tracker: ");
                fileLogger.Error(ex.ToString());
            }
        }        
    }
}
