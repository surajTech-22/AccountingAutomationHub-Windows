namespace AccountingAutomationHub.Domain.Masters;

/// <summary>
/// AAH-owned unit authority used by Sales/Purchase import and quantity conversion.
/// Tally is not a conversion authority. Canonical final units are the GST/Tally UQC codes;
/// common non-UQC source units are understood only as conversion inputs and are converted
/// into a canonical final unit before posting/export.
/// </summary>
public static class AahUnitCatalog
{
    public sealed record UnitDefinition(
        string Code,
        string Description,
        string Dimension = "",
        decimal ScaleToBase = 0m,
        int DecimalPlaces = 3)
    {
        public string ReportingUqc => $"{Code}-{Description}";
        public string Display => ReportingUqc;
        public bool HasDeterministicMeasure => Dimension.Length > 0 && ScaleToBase > 0m;
    }

    private sealed record SourceMeasure(string Dimension, decimal ScaleToBase, string PreferredCanonical);

    private static readonly UnitDefinition[] All =
    [
        new("BAG", "BAGS", DecimalPlaces: 0),
        new("BAL", "BALE", DecimalPlaces: 0),
        new("BDL", "BUNDLES", DecimalPlaces: 0),
        new("BKL", "BUCKLES", DecimalPlaces: 0),
        new("BOU", "BILLION OF UNITS", "COUNT", 1_000_000_000m, 0),
        new("BOX", "BOX", DecimalPlaces: 0),
        new("BTL", "BOTTLES", DecimalPlaces: 0),
        new("BUN", "BUNCHES", DecimalPlaces: 0),
        new("CAN", "CANS", DecimalPlaces: 0),
        new("CBM", "CUBIC METERS", "VOLUME_ML", 1_000_000m, 4),
        new("CCM", "CUBIC CENTIMETERS", "VOLUME_ML", 1m, 4),
        new("CMS", "CENTIMETERS", "LENGTH_M", 0.01m, 4),
        new("CTN", "CARTONS", DecimalPlaces: 0),
        new("DOZ", "DOZENS", "COUNT", 12m, 3),
        new("DRM", "DRUMS", DecimalPlaces: 0),
        new("GGK", "GREAT GROSS", "COUNT", 1_728m, 3),
        new("GMS", "GRAMMES", "MASS_G", 1m, 4),
        new("GRS", "GROSS", "COUNT", 144m, 3),
        new("GYD", "GROSS YARDS", "LENGTH_M", 131.6736m, 4),
        new("KGS", "KILOGRAMS", "MASS_G", 1_000m, 4),
        new("KLR", "KILOLITRE", "VOLUME_ML", 1_000_000m, 4),
        new("KME", "KILOMETRE", "LENGTH_M", 1_000m, 4),
        new("LTR", "LITRES", "VOLUME_ML", 1_000m, 4),
        new("MLT", "MILILITRE", "VOLUME_ML", 1m, 4),
        new("MTR", "METERS", "LENGTH_M", 1m, 4),
        new("MTS", "METRIC TON", "MASS_G", 1_000_000m, 4),
        new("NOS", "NUMBERS", "COUNT", 1m, 3),
        new("OTH", "OTHERS", DecimalPlaces: 3),
        new("PAC", "PACKS", DecimalPlaces: 0),
        new("PCS", "PIECES", "COUNT", 1m, 3),
        new("PRS", "PAIRS", "COUNT", 2m, 3),
        new("QTL", "QUINTAL", "MASS_G", 100_000m, 4),
        new("ROL", "ROLLS", DecimalPlaces: 0),
        new("SET", "SETS", DecimalPlaces: 0),
        new("SQF", "SQUARE FEET", "AREA_SQM", 0.09290304m, 4),
        new("SQM", "SQUARE METERS", "AREA_SQM", 1m, 4),
        new("SQY", "SQUARE YARDS", "AREA_SQM", 0.83612736m, 4),
        new("TBS", "TABLETS", "COUNT", 1m, 3),
        new("TGM", "TEN GROSS", "COUNT", 1_440m, 3),
        new("THD", "THOUSANDS", "COUNT", 1_000m, 3),
        new("TON", "TONNES", "MASS_G", 1_000_000m, 4),
        new("TUB", "TUBES", DecimalPlaces: 0),
        new("UGS", "US GALLONS", "VOLUME_ML", 3_785.411784m, 4),
        new("UNT", "UNITS", "COUNT", 1m, 3),
        new("YDS", "YARDS", "LENGTH_M", 0.9144m, 4)
    ];

    private static readonly IReadOnlyDictionary<string, UnitDefinition> ByCode =
        All.ToDictionary(unit => unit.Code, StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, string> CanonicalAliases = BuildCanonicalAliases();
    private static readonly IReadOnlyDictionary<string, SourceMeasure> ExtraSourceMeasures = BuildExtraSources();

    public static IReadOnlyList<UnitDefinition> Definitions => All;
    public static IReadOnlyList<string> CanonicalUnitCodes { get; } = All.Select(unit => unit.Code).ToArray();
    public static IReadOnlyList<string> DisplayOptions { get; } = All.Select(unit => unit.Display).ToArray();

    public static bool IsCanonical(string? value) =>
        ByCode.ContainsKey((value ?? string.Empty).Trim());

    public static bool TryGetDefinition(string? value, out UnitDefinition definition)
    {
        definition = default!;
        var text = (value ?? string.Empty).Trim();
        if (ByCode.TryGetValue(text, out var direct))
        {
            definition = direct;
            return true;
        }

        var key = Key(text);
        if (key.Length > 0 && CanonicalAliases.TryGetValue(key, out var code) && ByCode.TryGetValue(code, out direct))
        {
            definition = direct;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Resolves an imported/source unit to AAH's preferred canonical final unit and the
    /// quantity factor needed to convert 1 source unit into final units. Unknown package
    /// relationships deliberately return false; AAH never guesses package contents.
    /// </summary>
    public static bool TryResolvePreferred(string? source, out string canonicalCode, out decimal factor)
    {
        canonicalCode = string.Empty;
        factor = 1m;
        var text = (source ?? string.Empty).Trim();
        if (text.Length == 0) return false;

        if (TryGetDefinition(text, out var direct))
        {
            canonicalCode = direct.Code;
            factor = 1m;
            return true;
        }

        var key = Key(text);
        if (!ExtraSourceMeasures.TryGetValue(key, out var sourceMeasure) ||
            !ByCode.TryGetValue(sourceMeasure.PreferredCanonical, out var target) ||
            !target.HasDeterministicMeasure ||
            !target.Dimension.Equals(sourceMeasure.Dimension, StringComparison.OrdinalIgnoreCase))
            return false;

        canonicalCode = target.Code;
        factor = sourceMeasure.ScaleToBase / target.ScaleToBase;
        return factor > 0m;
    }

    /// <summary>
    /// Returns the exact AAH-owned quantity factor. For deterministic physical/count units
    /// user-entered factors are ignored by callers. For non-deterministic packages (BOX,
    /// BAG, PAC, etc.) this returns false unless source and final are the same canonical unit.
    /// </summary>
    public static bool TryGetFactor(string? from, string? to, out decimal factor)
    {
        factor = 1m;
        if (TryGetDefinition(from, out var fromDefinition) &&
            TryGetDefinition(to, out var toDefinition) &&
            fromDefinition.Code.Equals(toDefinition.Code, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!TryMeasure(from, out var source) || !TryMeasure(to, out var target))
            return false;
        if (!source.Dimension.Equals(target.Dimension, StringComparison.OrdinalIgnoreCase) ||
            source.ScaleToBase <= 0m || target.ScaleToBase <= 0m)
            return false;
        factor = source.ScaleToBase / target.ScaleToBase;
        return factor > 0m;
    }

    public static StockUnitRecord CreateUnitRecord(
        long clientId,
        string financialYear,
        string canonicalCode,
        string company,
        string source = "AAH Standard Unit")
    {
        if (!TryGetDefinition(canonicalCode, out var definition))
            throw new InvalidOperationException($"'{canonicalCode}' is not an AAH standard Unit.");
        return new StockUnitRecord
        {
            ClientId = clientId,
            FinancialYear = financialYear,
            Name = definition.Code,
            FormalName = definition.Description,
            DecimalPlaces = Math.Clamp(definition.DecimalPlaces, 0, 4),
            ReportingUqc = definition.ReportingUqc,
            Conversion = 1m,
            Company = company,
            Source = source,
            IsUserEdited = false
        };
    }

    private static bool TryMeasure(string? value, out SourceMeasure measure)
    {
        measure = default!;
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0) return false;
        if (TryGetDefinition(text, out var definition) && definition.HasDeterministicMeasure)
        {
            measure = new SourceMeasure(definition.Dimension, definition.ScaleToBase, definition.Code);
            return true;
        }
        if (ExtraSourceMeasures.TryGetValue(Key(text), out var extra))
        {
            measure = extra;
            return true;
        }
        return false;
    }

    private static IReadOnlyDictionary<string, string> BuildCanonicalAliases()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string code, params string[] aliases)
        {
            foreach (var alias in aliases.Prepend(code))
            {
                var key = Key(alias);
                if (key.Length > 0) result[key] = code;
            }
        }

        foreach (var unit in All) Add(unit.Code, unit.Description, unit.Display);
        Add("BAG", "BAG", "BAGS");
        Add("BAL", "BALE", "BALES");
        Add("BDL", "BUNDLE", "BUNDLES");
        Add("BKL", "BUCKLE", "BUCKLES");
        Add("BOX", "BOX", "BOXES");
        Add("BTL", "BOTTLE", "BOTTLES");
        Add("BUN", "BUNCH", "BUNCHES");
        Add("CAN", "CAN", "CANS");
        Add("CTN", "CARTON", "CARTONS");
        Add("DRM", "DRUM", "DRUMS");
        Add("PAC", "PACK", "PACKS", "PACKET", "PACKETS");
        Add("ROL", "ROLL", "ROLLS");
        Add("SET", "SET", "SETS");
        Add("TBS", "TABLET", "TABLETS");
        Add("TUB", "TUBE", "TUBES");
        Add("GGK", "GGR", "Great Gross");
        Add("GMS", "G", "GM", "GRAM", "GRAMS", "GRAMME", "GRAMMES");
        Add("KGS", "KG", "KILOGRAM", "KILOGRAMS");
        Add("KLR", "KL", "KILOLITER", "KILOLITERS", "KILOLITRE", "KILOLITRES");
        Add("KME", "KM", "KILOMETER", "KILOMETERS", "KILOMETRE", "KILOMETRES");
        Add("LTR", "L", "LT", "LITRE", "LITRES", "LITER", "LITERS");
        Add("MLT", "ML", "MLS", "MILLILITRE", "MILLILITRES", "MILLILITER", "MILLILITERS");
        Add("MTR", "M", "METRE", "METRES", "METER", "METERS");
        Add("MTS", "MT", "METRIC TON", "METRIC TONNE", "METRIC TONS", "METRIC TONNES");
        Add("NOS", "NO", "NOS", "NUMBER", "NUMBERS");
        Add("PCS", "PC", "PCS", "PCE", "PIECE", "PIECES", "EA", "EACH");
        Add("PRS", "PAIR", "PAIRS");
        Add("QTL", "QTLs", "QUINTAL", "QUINTALS");
        Add("TON", "TON", "TONS", "TONNE", "TONNES");
        Add("UNT", "UNIT", "UNITS");
        Add("YDS", "YD", "YARD", "YARDS");
        Add("CMS", "CM", "CENTIMETER", "CENTIMETERS", "CENTIMETRE", "CENTIMETRES");
        Add("SQF", "SQ FT", "SQFT", "FT2", "SQUARE FOOT", "SQUARE FEET");
        Add("SQM", "SQ M", "SQM", "M2", "SQUARE METER", "SQUARE METERS", "SQUARE METRE", "SQUARE METRES");
        Add("SQY", "SQ YD", "SQYD", "YD2", "SQUARE YARD", "SQUARE YARDS");
        Add("CBM", "M3", "CU M", "CUM", "CUBIC METRE", "CUBIC METRES", "CUBIC METER", "CUBIC METERS");
        Add("CCM", "CC", "CM3", "CU CM", "CUCM", "CUBIC CENTIMETRE", "CUBIC CENTIMETRES", "CUBIC CENTIMETER", "CUBIC CENTIMETERS");
        Add("UGS", "US GALLON", "US GALLONS", "USGAL");
        return result;
    }

    private static IReadOnlyDictionary<string, SourceMeasure> BuildExtraSources()
    {
        var result = new Dictionary<string, SourceMeasure>(StringComparer.OrdinalIgnoreCase);
        void Add(string preferred, string dimension, decimal scale, params string[] aliases)
        {
            foreach (var alias in aliases)
            {
                var key = Key(alias);
                if (key.Length > 0) result[key] = new SourceMeasure(dimension, scale, preferred);
            }
        }

        // Mass (base = gram)
        Add("GMS", "MASS_G", 0.001m, "MG", "MILLIGRAM", "MILLIGRAMS");
        Add("GMS", "MASS_G", 0.2m, "CARAT", "CARATS", "CT");
        Add("KGS", "MASS_G", 453.59237m, "LB", "LBS", "POUND", "POUNDS");
        Add("GMS", "MASS_G", 28.349523125m, "OZ", "OUNCE", "OUNCES");

        // Length (base = metre)
        Add("MTR", "LENGTH_M", 0.001m, "MM", "MILLIMETER", "MILLIMETERS", "MILLIMETRE", "MILLIMETRES");
        Add("MTR", "LENGTH_M", 0.0254m, "IN", "INCH", "INCHES");
        Add("MTR", "LENGTH_M", 0.3048m, "FT", "FOOT", "FEET", "RUNNING FOOT", "RUNNING FEET");

        // Area (base = square metre)
        Add("SQM", "AREA_SQM", 0.000001m, "SQ MM", "SQMM", "SQUARE MILLIMETER", "SQUARE MILLIMETRE");
        Add("SQM", "AREA_SQM", 0.0001m, "SQ CM", "SQCM", "SQUARE CENTIMETER", "SQUARE CENTIMETRE");
        Add("SQM", "AREA_SQM", 4_046.8564224m, "ACRE", "ACRES");
        Add("SQM", "AREA_SQM", 10_000m, "HECTARE", "HECTARES", "HA");

        // Volume (base = millilitre)
        Add("LTR", "VOLUME_ML", 4_546.09m, "IMPERIAL GALLON", "IMPERIAL GALLONS", "IMP GAL", "IMPGAL");
        Add("MLT", "VOLUME_ML", 16.387064m, "CUBIC INCH", "CUBIC INCHES", "CU IN", "CUIN");
        Add("CBM", "VOLUME_ML", 28_316.846592m, "CUBIC FOOT", "CUBIC FEET", "CU FT", "CUFT");

        return result;
    }

    private static string Key(string? value) =>
        string.Concat((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant));
}
