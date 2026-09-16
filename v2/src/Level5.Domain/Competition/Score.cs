using Level5.Domain.Common;

namespace Level5.Domain.Competition;

/// <summary>An attempt's numeric result. Higher is better; comparison policy lives on <see cref="GameRound"/>.</summary>
public readonly record struct Score(int Value)
{
    public static Score Of(int value)
    {
        if (value < 0)
        {
            throw new InvalidScoreException("Score cannot be negative.");
        }

        return new Score(value);
    }
}

public sealed class InvalidScoreException : DomainException
{
    public InvalidScoreException(string message) : base(message)
    {
    }
}
