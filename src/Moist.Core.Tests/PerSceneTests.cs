using Moist.Core.Quality;
using Moist.Services;

namespace Moist.Core.Tests;

/// <summary>
/// Calibration selection and the complexity model. No encoding.
/// </summary>
public class PerSceneTests
{
    // ---------------- choosing which scenes to search ----------------

    [Fact]
    public void CalibrationCoversTheComplexityRange()
    {
        // Scenes are picked by how different they are, not where they sit in the
        // timeline — ten similar scenes teach the model nothing that one would not.
        double[] complexity = [100, 105, 110, 108, 102, 50_000, 25_000];

        var chosen = PerSceneOptimizer.ChooseCalibrationScenes(complexity, 3);

        Assert.Equal(3, chosen.Count);
        // The extremes must be in there or the model has to extrapolate.
        Assert.Contains(chosen, i => complexity[i] <= 105);
        Assert.Contains(chosen, i => complexity[i] >= 25_000);
    }

    [Fact]
    public void CalibrationNeverAsksForMoreScenesThanExist()
    {
        var chosen = PerSceneOptimizer.ChooseCalibrationScenes([10, 20], 5);

        Assert.Equal(2, chosen.Count);
        Assert.Equal([0, 1], chosen);
    }

    [Fact]
    public void ASingleCalibrationScenePicksTheMiddle()
    {
        // The extremes would bias the model; the median is the safer single sample.
        var chosen = PerSceneOptimizer.ChooseCalibrationScenes([1, 100, 10_000], 1);

        Assert.Single(chosen);
        Assert.Equal(1, chosen[0]);
    }

    [Fact]
    public void CalibrationReturnsScenesInTimelineOrder()
    {
        // Purely so progress reporting reads sensibly.
        var chosen = PerSceneOptimizer.ChooseCalibrationScenes([9000, 10, 500, 30_000], 3);

        Assert.Equal(chosen.OrderBy(i => i), chosen);
    }

    // ---------------- the complexity model ----------------

    [Fact]
    public void MoreComplexScenesGetALowerCrf()
    {
        // The whole premise: a busy scene needs more bits for the same quality.
        var model = ComplexityModel.Fit([(100.0, 34.0), (10_000.0, 20.0)]);

        var simple = model.Predict(100);
        var complex = model.Predict(10_000);

        Assert.True(complex < simple, $"complex scene got CRF {complex:F1}, simple got {simple:F1}");
        Assert.Equal(34, simple, 1);
        Assert.Equal(20, complex, 1);
    }

    [Fact]
    public void ModelInterpolatesBetweenCalibrationPoints()
    {
        var model = ComplexityModel.Fit([(100.0, 34.0), (10_000.0, 20.0)]);

        // Geometric midpoint, since the fit is in the log domain.
        var middle = model.Predict(1000);

        Assert.InRange(middle, 25, 29);
    }

    [Fact]
    public void ASingleCalibrationPointGivesAConstant()
    {
        var model = ComplexityModel.Fit([(500.0, 26.0)]);

        Assert.Equal(26, model.Predict(1), 1);
        Assert.Equal(26, model.Predict(1_000_000), 1);
    }

    [Fact]
    public void UniformComplexityGivesTheAverageCrf()
    {
        // No gradient to fit, so don't invent one.
        var model = ComplexityModel.Fit([(500.0, 24.0), (500.0, 26.0)]);

        Assert.Equal(25, model.Predict(500), 1);
        Assert.Equal(25, model.Predict(50_000), 1);
    }

    [Fact]
    public void ModelSurvivesZeroComplexity()
    {
        // A completely black scene can encode to nothing; log(0) must not appear.
        var model = ComplexityModel.Fit([(0.0, 40.0), (10_000.0, 20.0)]);

        var predicted = model.Predict(0);
        Assert.True(double.IsFinite(predicted), $"predicted {predicted}");
    }

    [Fact]
    public void NoCalibrationPointsFallsBackToSomethingSane()
    {
        var model = ComplexityModel.Fit([]);
        var predicted = model.Predict(1000);

        Assert.True(double.IsFinite(predicted));
        Assert.InRange(predicted, 0, 51);
    }

    // ---------------- result reporting ----------------

    [Fact]
    public void ResultSummarisesHowEachSceneWasDecided()
    {
        var result = new PerSceneResult
        {
            Assignments =
            [
                new SceneAssignment { ChunkIndex = 0, Crf = 20, Complexity = 9000, Calibrated = true },
                new SceneAssignment { ChunkIndex = 1, Crf = 30, Complexity = 200 },
                new SceneAssignment { ChunkIndex = 2, Crf = 26, Complexity = 900, Calibrated = true, Corrected = true },
            ],
            Metric = QualityMetric.Vmaf,
            Target = 95,
        };

        Assert.Equal(20, result.MinCrf);
        Assert.Equal(30, result.MaxCrf);
        Assert.Equal(2, result.CalibratedCount);
        Assert.Equal(1, result.CorrectedCount);
        Assert.Contains("corrected", result.Summary);
    }

    [Fact]
    public void AssignmentsConvertToPerChunkQuality()
    {
        var result = new PerSceneResult
        {
            Assignments =
            [
                new SceneAssignment { ChunkIndex = 0, Crf = 20, Complexity = 1 },
                new SceneAssignment { ChunkIndex = 1, Crf = 30, Complexity = 1 },
            ],
            Metric = QualityMetric.Vmaf,
            Target = 95,
        };

        var quality = result.ChunkQuality;

        Assert.Equal(20, quality[0]);
        Assert.Equal(30, quality[1]);
    }

    [Fact]
    public void EachSceneExplainsHowItWasDecided()
    {
        Assert.Equal("searched", new SceneAssignment { ChunkIndex = 0, Crf = 20, Complexity = 1, Calibrated = true }.Explanation);
        Assert.Equal("modelled", new SceneAssignment { ChunkIndex = 0, Crf = 20, Complexity = 1 }.Explanation);
        Assert.Equal("corrected", new SceneAssignment { ChunkIndex = 0, Crf = 20, Complexity = 1, Calibrated = true, Corrected = true }.Explanation);
    }

    [Fact]
    public void UniformAssignmentsReadAsSuchRatherThanAsARange()
    {
        var result = new PerSceneResult
        {
            Assignments =
            [
                new SceneAssignment { ChunkIndex = 0, Crf = 23, Complexity = 1 },
                new SceneAssignment { ChunkIndex = 1, Crf = 23, Complexity = 1 },
            ],
            Metric = QualityMetric.Vmaf,
            Target = 95,
        };

        Assert.Contains("every scene at CRF 23", result.Summary);
    }
}
