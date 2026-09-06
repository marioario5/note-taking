namespace NoteTaker.Core.Search;

public static class VectorMath
{
    public static float[] Normalize(float[] vector)
    {
        double sum = 0;
        foreach (var value in vector)
        {
            sum += value * (double)value;
        }

        var magnitude = Math.Sqrt(sum);
        if (magnitude <= double.Epsilon)
        {
            return vector;
        }

        var result = new float[vector.Length];
        for (var i = 0; i < vector.Length; i++)
        {
            result[i] = (float)(vector[i] / magnitude);
        }

        return result;
    }

    /// <summary>Dot product. Vectors are stored normalized, so this is cosine similarity.</summary>
    public static double Similarity(float[] a, float[] b)
    {
        if (a.Length == 0 || a.Length != b.Length)
        {
            return 0d;
        }

        double dot = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * (double)b[i];
        }

        return dot;
    }

    public static byte[] ToBytes(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static float[] FromBytes(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return [];
        }

        var vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
        return vector;
    }
}
