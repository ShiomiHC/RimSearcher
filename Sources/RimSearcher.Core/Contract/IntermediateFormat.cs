namespace RimSearcher.Contract
{
    public static class IntermediateFormat
    {
        public const int FormatVersion = 5;

        public const string FileExtension = ".rsx.jsonl.gz";

        public const string CommandLineSwitch = "rimsearcher-export";

        public const string SkipEconomySwitch = "rimsearcher-no-economy";

        public const string ExporterPackageId = "rimsearcher.datamod";

        public static bool IsExporterPackageId(string id)
        {
            return id != null
                && (string.Equals(id, ExporterPackageId, System.StringComparison.OrdinalIgnoreCase)
                    || id.StartsWith(ExporterPackageId + ".", System.StringComparison.OrdinalIgnoreCase));
        }

        public const string KindMeta = "meta";
        public const string KindDef = "def";
        public const string KindDefInjection = "definj";
        public const string KindKeyed = "keyed";
        public const string KindXmlNode = "xmlnode";
        public const string KindEconomy = "economy";
        public const string KindXmlWritten = "xmlwritten";
        public const string KindTypeFields = "typefields";
        public const string KindInjKey = "injkey";
        public const string KindShaderSource = "shadersrc";
        public const string KindEnd = "end";

        public const string KeyKind = "kind";
        public const string KeyFormatVersion = "format_version";
        public const string KeyExporterVersion = "exporter_version";
        public const string KeyExportedAtUtc = "exported_at_utc";
        public const string KeyGameVersion = "game_version";
        public const string KeyLanguage = "language";
        public const string KeyMods = "mods";
        public const string KeyLimits = "limits";
        public const string KeyModSettingsHash = "mod_settings_hash";

        public const string KeyPackageId = "package_id";
        public const string KeyName = "name";
        public const string KeyVersion = "version";

        public const string KeyAssemblies = "assemblies";
        public const string KeyMvid = "mvid";

        public const string KeyDefType = "def_type";
        public const string KeyDefName = "def_name";
        public const string KeyLabel = "label";
        public const string KeyDescription = "description";
        public const string KeySourceMod = "source_mod";
        public const string KeySourceFile = "source_file";
        public const string KeyGenerated = "generated";
        public const string KeyClass = "class";
        public const string KeyFields = "fields";
        public const string KeyFieldsTruncated = "fields_truncated";

        public const string KeyTruncatedByCap = "truncated_by_cap";

        public const string KeyTruncatedByLength = "truncated_by_length";

        public const string KeyTruncatedByDepth = "truncated_by_depth";

        public const string KeyTruncatedByItems = "truncated_by_items";

        public const string KeyTruncatedByError = "truncated_by_error";

        public const string KeyPath = "path";
        public const string KeyTranslated = "translated";
        public const string KeyOriginal = "original";

        public const string KeyInjected = "injected";

        public const string KeyTimingsMs = "timings_ms";

        public static class TimingKeys
        {
            public const string Economy = "economy";
            public const string Defs = "defs";
            public const string TypeFields = "type_fields";
            public const string Injections = "injections";
            public const string InjKeys = "inj_keys";
            public const string InjKeysEmit = "inj_keys_emit";
            public const string Keyed = "keyed";
            public const string XmlNodes = "xml_nodes";
            public const string Total = "total";
        }

        public const string KeyKeyedKey = "key";
        public const string KeySourceLine = "source_line";
        public const string KeyPlaceholder = "placeholder";

        public const string KeySuggestedPath = "suggested_path";

        public const string KeyTranslationAllowed = "translation_allowed";

        public const string KeyFullListTranslationAllowed = "full_list_translation_allowed";

        public const string KeyIsCollection = "is_collection";

        public const string KeyParentName = "parent_name";
        public const string KeyAbstract = "abstract";
        public const string KeyPatchOps = "patch_ops";
        public const string KeyPatchOpsDefName = "patch_ops_defname";
        public const string KeyPatchOpsLabel = "patch_ops_label";

        public const string KeyPaths = "paths";
        public const string KeyTexts = "texts";
        public const string KeyPatched = "patched";
        public const string KeyPatchRoute = "patch_route";
        public const string PatchRouteHarmony = "harmony";
        public const string PatchRouteReplay = "replay";
        public const string PatchRouteNone = "none";
        public const string KeyNoInherit = "no_inherit";
        public const string KeyNodeKey = "node_key";
        public const string KeyKeyIsName = "key_is_name";

        public const string KeyEconomyMod = "mod";

        public const string KeyEconomyCategory = "category";
        public const string KeyEconomyMarketValue = "market_value";
        public const string KeyEconomyProducible = "producible";
        public const string KeyEconomyMadeFromStuff = "made_from_stuff";
        public const string KeyEconomyIsWeapon = "is_weapon";
        public const string KeyEconomyIsApparel = "is_apparel";
        public const string KeyEconomyMarketValueDefined = "market_value_defined";

        public const string KeyEconomyCalcState = "calc_state";

        public const string EconomyCalcNotProducible = "not_producible";
        public const string EconomyCalcUsed = "used";
        public const string EconomyCalcRecipe = "recipe";
        public const string EconomyCalcOk = "ok";

        public const string KeyEconomyCalculatedMarketValue = "calculated_market_value";

        public const string KeyEconomyCostToMake = "cost_to_make";

        public const string KeyEconomyProfit = "profit";

        public const string KeyEconomyProfitRate = "profit_rate";

        public const string KeyEconomyWorkToProduce = "work_to_produce";

        public const string KeyEconomyCostList = "cost_list";

        public const string KeyEconomyCostDifficultyVar = "cost_difficulty_var";

        public const string KeyEconomyCostDifficultyInverted = "cost_difficulty_inverted";

        public const string KeyEconomyCostChain = "cost_chain";
        public const string KeyEconomyThingDef = "thing_def";
        public const string KeyEconomyCount = "count";
        public const string KeyEconomyUnitValue = "unit_value";
        public const string KeyEconomyChainEnd = "chain_end";

        public const string KeyEconomyChainEndShare = "chain_end_share";

        public const string KeyEconomyCostDeep = "cost_deep";

        public const string KeyEconomyProfitDeep = "profit_deep";

        public const string KeyEconomyRecipeCandidates = "recipe_candidates";
        public const string KeyEconomyProductCount = "product_count";
        public const string KeyEconomyWorkAmount = "work_amount";
        public const string KeyEconomySelfReferential = "self_referential";

        public const string KeyShaderStage = "stage";
        public const string ShaderStageVertex = "vertex";
        public const string ShaderStageFragment = "fragment";
        public const string KeyShaderFile = "file";
        public const string KeyShaderResolvedPath = "resolved_path";
        public const string KeyShaderUnresolved = "unresolved";
        public const string ShaderUnresolvedNotFound = "not_found";
        public const string ShaderUnresolvedNoResolver = "no_resolver";

        public const string KeyRecords = "records";
        public const string KeyDefs = "defs";
        public const string KeyInjections = "injections";
        public const string KeyInjKeys = "inj_keys";
        public const string KeyKeyedCount = "keyed";
        public const string KeyXmlNodes = "xml_nodes";
        public const string KeyEconomyRows = "economy";

        public const string KeyEconomyState = "economy_state";

        public const string EconomyStateOk = "ok";
        public const string EconomyStateSkipped = "skipped";
        public const string EconomyStateUnavailable = "unavailable";

        public const string KeyEconomyError = "economy_error";

        public const string ImpliedDefsSourceFile = "ImpliedDefs";

        public const string ProgressFileSuffix = ".progress";

        public const string StageModClasses = "mod-classes";

        public const string StageExporting = "exporting";
    }

    public static class DefaultState
    {
        public const int Differs = 0;

        public const int Same = 1;

        public const int Unknown = 2;
    }

    public struct ExportedField
    {
        public string Path;
        public string Value;
        public int Default;

        public ExportedField(string path, string value, int defaultState)
        {
            Path = path;
            Value = value;
            Default = defaultState;
        }
    }

    public sealed class ExportLimits
    {
        public int MaxFieldDepth = 6;

        public int MaxInstanceFieldDepth = 28;

        public int MaxFieldValuesPerDef = 50000;

        public int MaxValueLength = 400;

        public int MaxCollectionItems = 200;
    }
}
