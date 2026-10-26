namespace ArtificialLife.Brain;

/// <summary>Ridge-initialized recursive least squares from observable transitions only.</summary>
public sealed class LearnedThermalTransition
{
    private double[] coefficients = new double[3];
    private readonly double[,] covariance = { { 10000, 0, 0 }, { 0, 10000, 0 }, { 0, 0, 10000 } };
    public long Samples { get; private set; }
    public double SquaredError { get; private set; }
    public double[] Coefficients => (double[])coefficients.Clone();
    public double Predict(double body, double field) => coefficients[0] * body + coefficients[1] * field + coefficients[2];
    public void Observe(double body, double observedNextField, double nextBody)
    {
        double[] x = [body, observedNextField, 1], px = new double[3];
        if (x.Any(v => !double.IsFinite(v)) || !double.IsFinite(nextBody)) throw new InvalidDataException("Non-finite model sample.");
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) px[i] += covariance[i, j] * x[j];
        double denominator = 1 + x.Select((v, i) => v * px[i]).Sum();
        double error = nextBody - Predict(body, observedNextField);
        for (int i = 0; i < 3; i++) coefficients[i] += px[i] * error / denominator;
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) covariance[i, j] -= px[i] * px[j] / denominator;
        Samples++; SquaredError += error * error;
    }
    public void Import(double[] values, long samples = 0, double squaredError = 0)
    {
        if (values.Length != 3 || values.Any(v => !double.IsFinite(v)) || samples < 0 || !double.IsFinite(squaredError) || squaredError < 0)
            throw new InvalidDataException("Invalid thermal model state.");
        coefficients = (double[])values.Clone(); Samples = samples; SquaredError = squaredError;
    }
}
