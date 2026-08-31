namespace Zashboard.Core.Sessions;

public readonly record struct SessionEpoch
{
    public SessionEpoch(long value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "A session epoch cannot be negative.");
        }

        Value = value;
    }

    public long Value { get; }

    public SessionEpoch Next() => new(checked(Value + 1));

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
