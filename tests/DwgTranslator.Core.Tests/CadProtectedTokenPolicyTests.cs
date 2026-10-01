using DwgTranslator.Application;

namespace DwgTranslator.Core.Tests;

[TestClass]
public sealed class CadProtectedTokenPolicyTests
{
    private static readonly string[] ExpectedTokens = ["{TAG_01}", "%%d", @"\P", @"\C1;"];
    private static readonly int[] ExpectedOrdinals = [0, 1, 2, 3];
    private static readonly string[] ExpectedLineBreakTokens = ["\r\n", "\n", "\r", @"\P"];
    private static readonly string[] ExpectedFlatFormattingTokens =
        ["{", @"\fArial|b0|i0|c0|p34;", @"\C1;", @"\P", @"\L", @"\l", "}"];
    private static readonly string[] ExpectedFormattingTokens =
        ["{", @"\A1;", @"\FArial|b1|i0;", @"\H0.7x;", @"\S1#2;", @"\U+00B0", @"\\", "}"];
    private static readonly string[] ExpectedLegacyAndMalformedTokens =
        ["{TAG_01}", "%%d", @"\P", @"\C1;", "}"];

    [TestMethod]
    public void ExtractsPlaceholdersAndCadControlsInSourceOrder()
    {
        var tokens = CadProtectedTokenPolicy.Extract(@"PUMP {TAG_01}%%d\PSAFETY \C1;RED");
        CollectionAssert.AreEqual(ExpectedTokens, tokens.Select(token => token.Token).ToArray());
        CollectionAssert.AreEqual(ExpectedOrdinals, tokens.Select(token => token.Ordinal).ToArray());
        Assert.AreEqual("Placeholder", tokens[0].Kind);
        Assert.IsTrue(tokens.Skip(1).All(token => token.Kind == "CadFormatControl"));
    }

    [TestMethod]
    public void ProtectsLiteralLineBreakEncodingExactly()
    {
        var tokens = CadProtectedTokenPolicy.Extract("ONE\r\nTWO\nTHREE\rFOUR\\PFIVE");

        CollectionAssert.AreEqual(ExpectedLineBreakTokens, tokens.Select(token => token.Token).ToArray());
        Assert.IsTrue(tokens.All(token => token.Kind == "CadFormatControl"));
    }

    [TestMethod]
    public void FlatMTextFormattingGroupKeepsVisibleTextEditable()
    {
        const string source = @"BEFORE {\fArial|b0|i0|c0|p34;PUMP \C1;RED\P\LON\l} AFTER";

        var protectedText = TranslationTokenAliases.Protect(source, CadProtectedTokenPolicy.Extract(source));

        Assert.IsTrue(protectedText.IsSuccess, protectedText.Error?.Code);
        Assert.AreEqual(
            "BEFORE ⟦T0⟧⟦T1⟧PUMP ⟦T2⟧RED⟦T3⟧⟦T4⟧ON⟦T5⟧⟦T6⟧ AFTER",
            protectedText.Value!.Text);
        CollectionAssert.AreEqual(
            ExpectedFlatFormattingTokens,
            protectedText.Value.Aliases.OrderBy(item => int.Parse(item.Key[1..], System.Globalization.CultureInfo.InvariantCulture)).Select(item => item.Value).ToArray());
        Assert.IsTrue(CadProtectedTokenPolicy.Extract(source).All(token => token.Kind == "CadFormatControl"));

        var restored = TranslationTokenAliases.Restore(
            "BEFORE ⟦T0⟧⟦T1⟧BOMBA ⟦T2⟧ROJO⟦T3⟧⟦T4⟧ENCENDIDO⟦T5⟧⟦T6⟧ AFTER",
            protectedText.Value.Aliases,
            protectedText.Value.Text);

        Assert.IsTrue(restored.IsSuccess, restored.Error?.Code);
        Assert.AreEqual(@"BEFORE {\fArial|b0|i0|c0|p34;BOMBA \C1;ROJO\P\LENCENDIDO\l} AFTER", restored.Value);
    }

    [TestMethod]
    public void MTextFormattingControlsArePreservedByteExactly()
    {
        const string source = @"{\A1;\FArial|b1|i0;\H0.7x;LEVEL \S1#2; \U+00B0 \\ PATH}";
        var tokens = CadProtectedTokenPolicy.Extract(source);

        CollectionAssert.AreEqual(
            ExpectedFormattingTokens,
            tokens.Select(token => token.Token).ToArray());
        Assert.IsTrue(tokens.All(token => token.Kind == "CadFormatControl"));

        var protectedText = TranslationTokenAliases.Protect(source, tokens);
        Assert.IsTrue(protectedText.IsSuccess, protectedText.Error?.Code);
        var restored = TranslationTokenAliases.Restore(
            protectedText.Value!.Text.Replace("LEVEL", "NIVEL", StringComparison.Ordinal).Replace("PATH", "RUTA", StringComparison.Ordinal),
            protectedText.Value.Aliases,
            protectedText.Value.Text);
        Assert.IsTrue(restored.IsSuccess, restored.Error?.Code);
        Assert.AreEqual(@"{\A1;\FArial|b1|i0;\H0.7x;NIVEL \S1#2; \U+00B0 \\ RUTA}", restored.Value);
    }

    [TestMethod]
    public void ToggleControlDoesNotConsumeVisibleTextEndingAtSemicolon()
    {
        const string source = @"{\LWARNING; KEEP CLEAR\l}";

        var protectedText = TranslationTokenAliases.Protect(source, CadProtectedTokenPolicy.Extract(source));

        Assert.IsTrue(protectedText.IsSuccess, protectedText.Error?.Code);
        Assert.AreEqual("⟦T0⟧⟦T1⟧WARNING; KEEP CLEAR⟦T2⟧⟦T3⟧", protectedText.Value!.Text);
        var restored = TranslationTokenAliases.Restore(
            "⟦T0⟧⟦T1⟧ADVERTENCIA; MANTENER LIBRE⟦T2⟧⟦T3⟧",
            protectedText.Value.Aliases,
            protectedText.Value.Text);
        Assert.IsTrue(restored.IsSuccess, restored.Error?.Code);
        Assert.AreEqual(@"{\LADVERTENCIA; MANTENER LIBRE\l}", restored.Value);
    }

    [TestMethod]
    public void FormattingGroupTokenTamperingFailsClosed()
    {
        const string source = @"{\fArial;PUMP}";
        var protectedText = TranslationTokenAliases.Protect(source, CadProtectedTokenPolicy.Extract(source));
        Assert.IsTrue(protectedText.IsSuccess, protectedText.Error?.Code);

        var tampered = TranslationTokenAliases.Restore(
            "⟦T0⟧⟦T1⟧BOMBA}",
            protectedText.Value!.Aliases,
            protectedText.Value.Text);

        Assert.IsFalse(tampered.IsSuccess);
        Assert.AreEqual("TOKEN_INTEGRITY_FAILED", tampered.Error!.Code);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("\t")]
    public void WhitespaceOnlyMTextSlotIsRestoredByteExactly(string translatedWhitespace)
    {
        const string source = "TITLE{\\H0.7x;  \t }";
        var protectedText = TranslationTokenAliases.Protect(source, CadProtectedTokenPolicy.Extract(source));
        Assert.IsTrue(protectedText.IsSuccess, protectedText.Error?.Code);
        var translated = $"TITULO⟦T0⟧⟦T1⟧{translatedWhitespace}⟦T2⟧";

        var restored = TranslationTokenAliases.Restore(
            translated,
            protectedText.Value!.Aliases,
            protectedText.Value.Text);

        Assert.IsTrue(restored.IsSuccess, restored.Error?.Code);
        Assert.AreEqual("TITULO{\\H0.7x;  \t }", restored.Value);
        CollectionAssert.AreEqual(
            CadProtectedTokenPolicy.Extract(source).Select(item => item.Token).ToArray(),
            CadProtectedTokenPolicy.Extract(restored.Value!).Select(item => item.Token).ToArray());
    }

    [TestMethod]
    public void WhitespaceOnlyMTextSlotRejectsVisibleTextInjection()
    {
        const string source = "TITLE{\\H0.7x;   }";
        var protectedText = TranslationTokenAliases.Protect(source, CadProtectedTokenPolicy.Extract(source));
        Assert.IsTrue(protectedText.IsSuccess, protectedText.Error?.Code);

        var restored = TranslationTokenAliases.Restore(
            "TITULO⟦T0⟧⟦T1⟧INJECTED⟦T2⟧",
            protectedText.Value!.Aliases,
            protectedText.Value.Text);

        Assert.IsFalse(restored.IsSuccess);
        Assert.AreEqual("TOKEN_INTEGRITY_FAILED", restored.Error!.Code);
    }

    [TestMethod]
    [DataRow("BOMBA⟦T0⟧⟦T1⟧⟦T2⟧", DisplayName = "visible text moved before group")]
    [DataRow("⟦T0⟧⟦T1⟧⟦T2⟧BOMBA", DisplayName = "visible text moved after group")]
    [DataRow("⟦T0⟧BOMBA⟦T1⟧⟦T2⟧", DisplayName = "visible text moved before formatting control")]
    [DataRow("⟦T0⟧⟦T1⟧⟦T2⟧", DisplayName = "translatable slot emptied")]
    public void FormattingGroupRejectsChangedTextSlotOccupancy(string translatedText)
    {
        const string source = @"{\fArial;PUMP}";
        var protectedText = TranslationTokenAliases.Protect(source, CadProtectedTokenPolicy.Extract(source));
        Assert.IsTrue(protectedText.IsSuccess, protectedText.Error?.Code);

        var restored = TranslationTokenAliases.Restore(
            translatedText,
            protectedText.Value!.Aliases,
            protectedText.Value.Text);

        Assert.IsFalse(restored.IsSuccess);
        Assert.AreEqual("TOKEN_INTEGRITY_FAILED", restored.Error!.Code);
    }

    [TestMethod]
    [DataRow(@"{\fArial;OUTER {\C1;INNER}}", DisplayName = "nested group")]
    [DataRow(@"{\fArial;UNTERMINATED", DisplayName = "unterminated group")]
    [DataRow(@"\{LITERAL\}", DisplayName = "escaped braces")]
    [DataRow(@"\{OUTER \{INNER\}\}", DisplayName = "nested escaped braces")]
    [DataRow(@"{\fArial;A\{B\}}", DisplayName = "escaped braces inside formatting group")]
    [DataRow(@"{\fArial;UNKNOWN \z CONTROL}", DisplayName = "unknown control inside formatting group")]
    [DataRow(@"{\fArial;UNKNOWN \z0; CONTROL}", DisplayName = "unknown terminated control inside formatting group")]
    [DataRow(@"{\z0;UNKNOWN OPENING CONTROL}", DisplayName = "unknown opening control")]
    public void AmbiguousBraceStructuresFailClosedAsOneProtectedToken(string source)
    {
        var tokens = CadProtectedTokenPolicy.Extract(source);

        Assert.AreEqual(1, tokens.Count);
        Assert.AreEqual(source, tokens[0].Token);
        var protectedText = TranslationTokenAliases.Protect(source, tokens);
        Assert.IsTrue(protectedText.IsSuccess, protectedText.Error?.Code);
        Assert.AreEqual(TranslationTokenAliases.Alias("T0"), protectedText.Value!.Text);
    }

    [TestMethod]
    public void ExistingPlaceholdersPercentCodesAndMalformedClosingBraceRemainProtected()
    {
        const string source = @"PUMP {TAG_01} %%d\P\C1;TAIL}";
        var tokens = CadProtectedTokenPolicy.Extract(source);

        CollectionAssert.AreEqual(
            ExpectedLegacyAndMalformedTokens,
            tokens.Select(token => token.Token).ToArray());
        Assert.AreEqual("Placeholder", tokens[0].Kind);
        Assert.IsTrue(tokens.Skip(1).All(token => token.Kind == "CadFormatControl"));
        CollectionAssert.AreEqual(Enumerable.Range(0, tokens.Count).ToArray(), tokens.Select(token => token.Ordinal).ToArray());
    }
}
