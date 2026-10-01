using DwgTranslator.Application;
using DwgTranslator.Contracts;
using DwgTranslator.Domain;

namespace DwgTranslator.Core.Tests;

[TestClass]
public sealed class HumanReviewPolicyEditDiagnosticsTests
{
    [TestMethod]
    public void AuthorizedRealEditsIdentifyOnlyTrailingNewlineAsTokenViolation()
    {
        var cases = new[]
        {
            Case("25ee", "ENTRADA DE AIRE A LLOS MUROS, ESPESOR TOTAL ", "LUFTEINTRITT IN DIE WÄNDE, GESAMTDICKE ", true),
            Case("75fb", "ESC.  1 : 75", "MAßSTAB  1 : 75", true),
            Case("fe43", MTextSource, MTextEdit + "\n", false),
            Case("f689", "CANALES DE AMARRE USG 9.20 CAL. 26.  LOS ", "USG-ANSCHLUSSPROFILE 9.20 CAL. 26. DIE ", true)
        };

        foreach (var item in cases)
        {
            var result = HumanReviewPolicy.Approve(item.Source, new AcceptedTranslation(item.Source.SegmentId, item.Edit), item.Edit);
            Assert.AreEqual(item.ExpectedSuccess, result.IsSuccess, item.Source.SegmentId);
            if (!item.ExpectedSuccess) Assert.AreEqual("TOKEN_INTEGRITY_FAILED", result.Error!.Code);
        }
    }

    [TestMethod]
    public void RemovingOnlyUnauthorizedTrailingNewlineMakesMTextEditValid()
    {
        var item = Case("fe43", MTextSource, MTextEdit, true);
        var result = HumanReviewPolicy.Approve(item.Source, new AcceptedTranslation(item.Source.SegmentId, item.Edit), item.Edit);
        Assert.IsTrue(result.IsSuccess, result.Error?.Code);
    }

    [TestMethod]
    [DataRow(@"BOMBA{\fArial;}", DisplayName = "visible text moved before group")]
    [DataRow(@"{\fArial;}BOMBA", DisplayName = "visible text moved after group")]
    public void HumanReviewRejectsMovingVisibleTextOutsideMTextFormattingScope(string finalText)
    {
        var item = Case("abcd", @"{\fArial;PUMP}", finalText, false);

        var result = HumanReviewPolicy.Approve(item.Source, new AcceptedTranslation(item.Source.SegmentId, item.Edit), item.Edit);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("TOKEN_INTEGRITY_FAILED", result.Error!.Code);
    }

    [TestMethod]
    public void HumanReviewAllowsVisibleTextChangeInsideSameMTextFormattingScope()
    {
        var item = Case("abce", @"{\fArial;PUMP}", @"{\fArial;BOMBA}", true);

        var result = HumanReviewPolicy.Approve(item.Source, new AcceptedTranslation(item.Source.SegmentId, item.Edit), item.Edit);

        Assert.IsTrue(result.IsSuccess, result.Error?.Code);
    }

    [TestMethod]
    public void HumanReviewRehydratesWhitespaceOnlyMTextSlotBeforeReturningApproval()
    {
        var item = Case("abcf", "TITLE{\\H0.7x;  \t }", "TITULO{\\H0.7x;}", true);

        var result = HumanReviewPolicy.Approve(item.Source, new AcceptedTranslation(item.Source.SegmentId, item.Edit), item.Edit);

        Assert.IsTrue(result.IsSuccess, result.Error?.Code);
        Assert.AreEqual("TITULO{\\H0.7x;  \t }", result.Value!.FinalText);
    }

    [TestMethod]
    public void HumanReviewRejectsVisibleTextInjectedIntoWhitespaceOnlyMTextSlot()
    {
        var item = Case("abd0", "TITLE{\\H0.7x;   }", "TITULO{\\H0.7x;VISIBLE}", false);

        var result = HumanReviewPolicy.Approve(item.Source, new AcceptedTranslation(item.Source.SegmentId, item.Edit), item.Edit);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("TOKEN_INTEGRITY_FAILED", result.Error!.Code);
    }

    private static (CadTextSegment Source, string Edit, bool ExpectedSuccess) Case(
        string suffix, string source, string edit, bool expectedSuccess) =>
        (new CadTextSegment
        {
            SegmentId = "seg_sha256_" + suffix.PadRight(64, '0'),
            Entity = new CadEntityReference { Type = "MTEXT", Handle = "1", Space = "ModelSpace", Layout = null, BlockPath = [], Layer = "NOTES", SubIndex = 0 },
            SourceText = source,
            SourceTextHash = "sha256:" + new string('a', 64),
            LineBreakStyle = "None",
            ProtectedTokens = CadProtectedTokenPolicy.Extract(source).ToList(),
            FieldClassification = "None",
            State = "Extracted"
        }, edit, expectedSuccess);

    private const string MTextSource = "\\pxi-3,l3,t3;1.\tESTA INFORMACIÓN CORRESPONDE UNICAMENTE AL PROYECTO DE ASCENTY VESTA.\\P2.\tESTE PLANO SE COMPLEMENTA CON LOS PLANOS DE REFERENCIA.\\P3.\tEL CONTRATISTA ES RESPONSABLE DE TRABAJAR SIEMPRE CON UN JUEGO COMPLETO DE PLANOS.\\P\tCUALQUIER DUDA EN LA INTERPRETACIÓN DE LOS MISMOS, DEBERÁ CONSULTARSE CON LA DIRECCIÓN DE OBRA.\\P4.\tSI EL PLANO NO MIDE 36X24 PULGADAS, ENTONCES NO ESTA EN LA ESCALA INDICADA EN EL PIE DE PLANO.\\P5.\tLAS COTAS Y NIVELES SE PROPORCIONAN EN METROS, PIES, PULGADAS O MILIMETROS (SEGUN SE INDIQUE).\\P6.\tLAS COTAS RIGEN EL DIBUJO.\\P7.\tLOS PLANOS DEBERAN VERIFICARSE CON LOS CORRESPONDIENTES DE INSTALACIONES Y ESTRUCTURALES, CUALQUIER DISCREPANCIA DEBERÁ SER CONSULTADA CON LA DIRECCIÓN DE OBRA.\\P8.\tANTES DE CERRAR EL PISO, EL CONTRATISTA DEBE DEMOSTRAR EL SELLADO TOTAL DE LA LOSABASE CON PINTURA EPOXICA ANTIESTATICA DE DOS COMPONENTES.\\P9.\tSE REQUIERE UNA LIMPIEZA TECNICA DE GRADO INDUSTRIAL (HEPA VACUUM) BAJO EL PISO FALSO ANTES DE LA COLOCACION DE LAS BALDOSAS FINALES PARA CUMPLIR CON ISO CLASS 8.\\P10.\tTODA BALDOSA DE AJUSTE CORTADA A MENOS DEL 75% DE SU TAMAÑO ORIGINAL (<450MM) DEBE CONTAR CON UN PEDESTAL DE REFUERZO EN EL ANGULO DE CORTE.";
    private const string MTextEdit = "\\pxi-3,l3,t3;1.\tDIESE INFORMATIONEN BEZIEHEN SICH AUSSCHLIESSLICH AUF DAS PROJEKT ASCENTY VESTA.\\P2.\tDIESER PLAN WIRD DURCH DIE REFERENZPLÄNE ERGÄNZT.\\P3.\tDER AUFTRAGNEHMER IST DAFÜR VERANTWORTLICH, STETS MIT EINEM VOLLSTÄNDIGEN PLANSATZ ZU ARBEITEN.\\P\tBEI FRAGEN ZUR INTERPRETATION DIESER PLÄNE IST DIE BAULEITUNG ZU KONSULTIEREN.\\P4.\tWENN DER PLAN NICHT 36X24 ZOLL MISST, ENTSPRICHT ER NICHT DEM IM SCHRIFTFELD ANGEGEBENEN MAßSTAB.\\P5.\tDIE BEMAßUNGEN UND HÖHEN WERDEN IN METERN, FUSS, ZOLL ODER MILLIMETERN ANGEGEBEN (WIE ANGEGEBEN).\\P6.\tDIE BEMAßUNGEN SIND MAßGEBEND.\\P7.\tDIE PLÄNE SIND MIT DEN ENTSPRECHENDEN PLÄNEN DER TECHNISCHEN ANLAGEN UND DER TRAGWERKSPLANUNG ABZUGLEICHEN, JEDE ABWEICHUNG IST MIT DER BAULEITUNG ABZUSTIMMEN.\\P8.\tVOR DEM SCHLIESSEN DES BODENS MUSS DER AUFTRAGNEHMER DIE VOLLSTÄNDIGE ABDICHTUNG DER BODENPLATTE MIT ZWEIKOMPONENTIGER ANTISTATISCHER EPOXIDFARBE NACHWEISEN.\\P9.\tVOR DEM VERLEGEN DER ENDGÜLTIGEN PLATTEN IST UNTER DEM DOPPELBODEN EINE TECHNISCHE REINIGUNG IN INDUSTRIEQUALITÄT (HEPA-STAUBSAUGER) ERFORDERLICH, UM ISO-KLASSE 8 ZU ERFÜLLEN.\\P10.\tJEDE ZUGESCHNITTENE ANPASSUNGSPLATTE, DIE AUF WENIGER ALS 75% IHRER URSPRÜNGLICHEN GRÖSSE (<450MM) ZUGESCHNITTEN WIRD, MUSS AM SCHNITTWINKEL MIT EINEM VERSTÄRKUNGSSOCKEL VERSEHEN SEIN.";
}
