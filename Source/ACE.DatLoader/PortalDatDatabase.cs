
using ACE.DatLoader.FileTypes;

namespace ACE.DatLoader
{
    public class PortalDatDatabase : DatDatabase
    {
        public PortalDatDatabase(string filename, bool keepOpen = false) : base(filename, keepOpen)
        {
            BadData = ReadFromDat<BadData>(BadData.FILE_ID);
            ChatPoseTable = ReadFromDat<ChatPoseTable>(ChatPoseTable.FILE_ID);
            CharGen = ReadFromDat<CharGen>(CharGen.FILE_ID);
            ContractTable = ReadFromDat<ContractTable>(ContractTable.FILE_ID);
            GeneratorTable = ReadFromDat<GeneratorTable>(GeneratorTable.FILE_ID);
            MasterProperty = ReadFromDat<MasterProperty>(MasterProperty.FILE_ID);
            NameFilterTable = ReadFromDat<NameFilterTable>(NameFilterTable.FILE_ID);
            RegionDesc = ReadFromDat<RegionDesc>(RegionDesc.FILE_ID);
            SecondaryAttributeTable = ReadFromDat<SecondaryAttributeTable>(SecondaryAttributeTable.FILE_ID);
            SkillTable = ReadFromDat<SkillTable>(SkillTable.FILE_ID);
            SpellComponentsTable = ReadFromDat<SpellComponentsTable>(SpellComponentsTable.FILE_ID);
            SpellTable = ReadFromDat<SpellTable>(SpellTable.FILE_ID);
            TabooTable = ReadFromDat<TabooTable>(TabooTable.FILE_ID);
            XpTable = ReadFromDat<XpTable>(XpTable.FILE_ID);
        }

        /// <summary>
        /// A portal database with no backing file.
        /// <para />
        /// Every one of the 14 tables is a default-constructed instance, which is a valid empty
        /// table: they are plain classes whose collections are field-initialized. That is enough
        /// for the ~75 call sites that only need the table object to exist. Two of them are
        /// populated with generated data instead, because the arithmetic downstream indexes into
        /// them and would otherwise read empty:
        /// <list type="bullet">
        /// <item><description><see cref="RegionDesc"/> — supplies the land height table and the terrain/scene
        /// maps the physics geometry builder needs to construct a landblock at all.</description></item>
        /// <item><description><see cref="XpTable"/> — supplies the level and rank curves, with the retail
        /// entry counts so max level resolves to 275.</description></item>
        /// </list>
        /// See <see cref="DatManager.InitializeSynthesized"/>.
        /// </summary>
        public static PortalDatDatabase CreateSynthesized() => new SynthesizedPortalDatDatabase();

        protected PortalDatDatabase(bool _) : base()
        {
            BadData = new BadData();
            ChatPoseTable = new ChatPoseTable();
            CharGen = new CharGen();
            ContractTable = new ContractTable();
            GeneratorTable = new GeneratorTable();
            MasterProperty = new MasterProperty();
            NameFilterTable = new NameFilterTable();
            RegionDesc = SynthesizedDatabases.CreateRegionDesc();
            SecondaryAttributeTable = new SecondaryAttributeTable();
            SkillTable = new SkillTable();
            SpellComponentsTable = new SpellComponentsTable();
            SpellTable = new SpellTable();
            TabooTable = new TabooTable();
            XpTable = SynthesizedDatabases.CreateXpTable();
        }

        public BadData BadData { get; }
        public ChatPoseTable ChatPoseTable { get; }
        public CharGen CharGen { get; }
        public ContractTable ContractTable { get; }
        public GeneratorTable GeneratorTable { get; }
        public MasterProperty MasterProperty { get; }
        public NameFilterTable NameFilterTable { get; }
        public RegionDesc RegionDesc { get; }
        public SecondaryAttributeTable SecondaryAttributeTable { get; }
        public SkillTable SkillTable { get; }
        public SpellComponentsTable SpellComponentsTable { get; }
        public SpellTable SpellTable { get; }
        public TabooTable TabooTable { get; }
        public XpTable XpTable { get; }
    }
}
