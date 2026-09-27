using System.Collections.Generic;
using System.IO;

namespace ACE.DatLoader
{
    public class CellDatDatabase : DatDatabase
    {
        public CellDatDatabase(string filename, bool keepOpen = false) : base(filename, keepOpen)
        {
        }

        /// <summary>
        /// A cell database with no backing file. Returns a synthesized flat landblock for any
        /// cell id. See <see cref="DatManager.InitializeSynthesized"/>.
        /// </summary>
        public static CellDatDatabase CreateSynthesized() => new SynthesizedCellDatDatabase();

        protected CellDatDatabase(bool _) : base()
        {
        }

        public void ExtractLandblockContents(string path)
        {
            foreach (KeyValuePair<uint, DatFile> entry in AllFiles)
            {
                string thisFolder = Path.Combine(path, (entry.Value.ObjectId >> 16).ToString("X4"));

                if (!Directory.Exists(thisFolder))
                    Directory.CreateDirectory(thisFolder);

                // Use the DatReader to get the file data - file blocks can extend over block size.
                DatReader dr = GetReaderForFile(entry.Value.ObjectId);

                string hex = entry.Value.ObjectId.ToString("X8");
                string thisFile = Path.Combine(thisFolder, hex + ".bin");
                File.WriteAllBytes(thisFile, dr.Buffer);
            }
        }
    }
}
