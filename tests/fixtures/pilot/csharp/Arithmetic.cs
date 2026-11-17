namespace Pilot;

// Byte-span fixture: café 🚀. This text precedes the declarations.
public static partial class Arithmetic
{
    /// <summary>Returns the input plus one when positive, otherwise zero.</summary>
    public static int Choose(int value)
    {
        int result;
        if (value > 0)
            result = Add(value, 1);
        else
            result = 0;
        return result;
    }

    public static int Add(int left, int right) => left + right;
}
