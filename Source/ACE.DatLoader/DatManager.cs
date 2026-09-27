using System.IO;

using log4net;

namespace ACE.DatLoader
{
    public static class DatManager
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private static string datFile;

        private static int count;

        // End of retail Iteration versions.
        private static int ITERATION_CELL = 982;
        private static int ITERATION_PORTAL = 2072;
        private static int ITERATION_HIRES = 497;
        private static int ITERATION_LANGUAGE = 994;
        public static CellDatDatabase CellDat { get; private set; }

        public static PortalDatDatabase PortalDat { get; private set; }
        public static DatDatabase HighResDat { get; private set; }
        public static LanguageDatDatabase LanguageDat { get; private set; }

        public static void Initialize(string datFileDirectory, bool keepOpen = false, bool loadCell = true)
        {
            var datDir = Path.GetFullPath(Path.Combine(datFileDirectory));

            if (loadCell)
            {
                try
                {
                    datFile = Path.Combine(datDir, "client_cell_1.dat");
                    CellDat = new CellDatDatabase(datFile, keepOpen);
                    count = CellDat.AllFiles.Count;
                    log.Info($"Successfully opened {datFile} file, containing {count} records, iteration {CellDat.Iteration}");
                    if (CellDat.Iteration != ITERATION_CELL)
                        log.Warn($"{datFile} iteration does not match expected end-of-retail version of {ITERATION_CELL}.");
                }
                catch (FileNotFoundException ex)
                {
                    log.Error($"An exception occured while attempting to open {datFile} file!  This needs to be corrected in order for Landblocks to load!");
                    log.Error($"Exception: {ex.Message}");
                }
            }

            try
            {
                datFile = Path.Combine(datDir, "client_portal.dat");
                PortalDat = new PortalDatDatabase(datFile, keepOpen);
                PortalDat.SkillTable.AddRetiredSkills();
                count = PortalDat.AllFiles.Count;
                log.Info($"Successfully opened {datFile} file, containing {count} records, iteration {PortalDat.Iteration}");
                if (PortalDat.Iteration != ITERATION_PORTAL)
                    log.Warn($"{datFile} iteration does not match expected end-of-retail version of {ITERATION_PORTAL}.");
            }
            catch (FileNotFoundException ex)
            {
                log.Error($"An exception occured while attempting to open {datFile} file!\n\n *** Please check your 'DatFilesDirectory' setting in the config.js file. ***\n *** ACE will not run properly without this properly configured! ***\n");
                log.Error($"Exception: {ex.Message}");
            }

            // Load the client_highres.dat file. This is not required for ACE operation, so no exception needs to be generated.
            datFile = Path.Combine(datDir, "client_highres.dat");
            if (File.Exists(datFile))
            {
                HighResDat = new DatDatabase(datFile, keepOpen);
                count = HighResDat.AllFiles.Count;
                log.Info($"Successfully opened {datFile} file, containing {count} records, iteration {HighResDat.Iteration}");
                if (HighResDat.Iteration != ITERATION_HIRES)
                    log.Warn($"{datFile} iteration does not match expected end-of-retail version of {ITERATION_HIRES}.");
            }

            try
            {
                datFile = Path.Combine(datDir, "client_local_English.dat");
                LanguageDat = new LanguageDatDatabase(datFile, keepOpen);
                count = LanguageDat.AllFiles.Count;
                log.Info($"Successfully opened {datFile} file, containing {count} records, iteration {LanguageDat.Iteration}");
                if(LanguageDat.Iteration != ITERATION_LANGUAGE)
                    log.Warn($"{datFile} iteration does not match expected end-of-retail version of {ITERATION_LANGUAGE}.");
            }
            catch (FileNotFoundException ex)
            {
                log.Error($"An exception occured while attempting to open {datFile} file!\n\n *** Please check your 'DatFilesDirectory' setting in the config.json file. ***\n *** ACE will not run properly without this properly configured! ***\n");
                log.Error($"Exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Initializes the dat databases with synthesized stand-ins instead of reading any .dat
        /// files. This is the dat-free mode behind <c>Server.StartWithoutDats</c>, intended for
        /// protocol work and client development.
        /// <para />
        /// Enough of the world comes up to log in, list characters, and enter the game world, but
        /// the world is a featureless flat plane with no scenery and no terrain. Character creation
        /// is the one feature that still cannot work, because CharGen and the appearance palettes
        /// only exist in client_portal.dat; seed characters directly into the database instead.
        /// <para />
        /// client_highres.dat is already optional everywhere in the server, so it is left null here
        /// too, which is the same state a real shard is in when it does not ship the file.
        /// </summary>
        public static void InitializeSynthesized(bool loadCell = true)
        {
            if (loadCell)
            {
                CellDat = CellDatDatabase.CreateSynthesized();
                log.Warn("Dat-free mode: client_cell_1.dat is synthesized. Every landblock is flat, with no scenery or terrain.");
            }

            PortalDat = PortalDatDatabase.CreateSynthesized();
            PortalDat.SkillTable.AddRetiredSkills();
            log.Warn($"Dat-free mode: client_portal.dat is synthesized. Every table is empty, except the XP curves and the region description, which are generated. Reported as iteration {PortalDat.Iteration}.");

            LanguageDat = LanguageDatDatabase.CreateSynthesized();
            log.Warn($"Dat-free mode: client_local_English.dat is synthesized. Character titles resolve to no title. Reported as iteration {LanguageDat.Iteration}.");
        }
    }
}
