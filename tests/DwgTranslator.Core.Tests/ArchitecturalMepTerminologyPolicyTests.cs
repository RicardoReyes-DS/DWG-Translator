using DwgTranslator.Application;

namespace DwgTranslator.Core.Tests;

[TestClass]
public sealed class ArchitecturalMepTerminologyPolicyTests
{
    [TestMethod]
    [DataRow("N.P.T. +0.15", "FFL +0.15")]
    [DataRow("n p t -0.30", "FFL -0.30")]
    [DataRow("NPT +1.20", "FFL +1.20")]
    [DataRow("SOBRE NIVEL DE PISO TERMINADO +2.10", "AFFL +2.10")]
    public void AppliesCanonicalLabelsAndPreservesNumbers(string source, string expected)
    {
        var result = ArchitecturalMepTerminologyPolicy.Apply(source, new(null, null, null, null, "HVAC"));
        Assert.AreEqual(expected, result.Text);
        Assert.IsFalse(result.ErrorCodes.Contains("NUMERIC_INTEGRITY_FAILED"));
    }

    [TestMethod]
    public void UsesLongFormForLegend()
    {
        var result = ArchitecturalMepTerminologyPolicy.Apply("N.P.T. - NIVEL DE PISO TERMINADO", new(null, null, null, null, "HVAC"));
        Assert.AreEqual("FINISHED FLOOR LEVEL - FINISHED FLOOR LEVEL", result.Text);
    }

    [TestMethod]
    public void ResolvesCeilingRoofAndRaisedFloorOnlyWithContext()
    {
        Assert.AreEqual("FCL +2.70", ArchitecturalMepTerminologyPolicy.Apply("NTT +2.70", new("CEILING", null, null, null, "HVAC")).Text);
        Assert.AreEqual("FRL +4.20", ArchitecturalMepTerminologyPolicy.Apply("NTT +4.20", new("ROOF", null, null, null, "HVAC")).Text);
        Assert.AreEqual("RAFL +0.60", ArchitecturalMepTerminologyPolicy.Apply("NPF +0.60", new("RAISED ACCESS FLOOR", null, null, null, "HVAC")).Text);
        Assert.IsTrue(ArchitecturalMepTerminologyPolicy.Apply("NPF +0.60", new(null, null, null, null, "HVAC")).ErrorCodes.Contains("TERMINOLOGY_AMBIGUOUS"));
    }

    [TestMethod]
    public void PreservesMTextControlsAndRejectsResiduals()
    {
        const string source = @"\C1;N.P.T. +0.15\P";
        var result = ArchitecturalMepTerminologyPolicy.Apply(source, new(null, null, null, null, "HVAC"));
        Assert.AreEqual(@"\C1;FFL +0.15\P", result.Text);
        Assert.AreEqual(0, ArchitecturalMepTerminologyPolicy.Validate(source, result.Text, result.Matches).Count);
        Assert.IsTrue(ArchitecturalMepTerminologyPolicy.Validate(source, @"\C1;NPT +0.15\P", []).Contains("TERMINOLOGY_SPANISH_RESIDUAL"));
    }
}
