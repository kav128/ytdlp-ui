using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Ytdlp.Ui.Infrastructure.Persistence;

public sealed class UnixMillisecondsConverter() : ValueConverter<DateTimeOffset, long>(
    value => value.ToUnixTimeMilliseconds(),
    value => DateTimeOffset.FromUnixTimeMilliseconds(value))
{
    public static readonly UnixMillisecondsConverter Instance = new();
}
