using ACE.DatLoader.FileTypes;

namespace ACE.DatLoader
{
    public class LanguageDatDatabase : DatDatabase
    {
        public LanguageDatDatabase(string filename, bool keepOpen = false) : base(filename, keepOpen)
        {
            CharacterTitles = ReadFromDat<StringTable>(StringTable.CharacterTitle_FileID);
        }

        /// <summary>
        /// A language database with no backing file. <see cref="CharacterTitles"/> becomes an empty
        /// string table, so a title lookup resolves to "no title" instead of throwing.
        /// See <see cref="DatManager.InitializeSynthesized"/>.
        /// </summary>
        public static LanguageDatDatabase CreateSynthesized() => new SynthesizedLanguageDatDatabase();

        protected LanguageDatDatabase(bool _) : base()
        {
            CharacterTitles = new StringTable();
        }

        public StringTable CharacterTitles { get; }
    }
}
