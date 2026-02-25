using Dapper;
using AkariApi.Models;
using System.Data;

namespace AkariApi.Helpers;

public class StringArrayTypeHandler : SqlMapper.TypeHandler<string[]>
{
    public override void SetValue(IDbDataParameter parameter, string[]? value)
    {
        parameter.Value = value ?? Array.Empty<string>();
    }

    public override string[] Parse(object value) => (string[])value;
}

public class NullableStringArrayTypeHandler : SqlMapper.TypeHandler<string[]?>
{
    public override void SetValue(IDbDataParameter parameter, string[]? value)
    {
        parameter.Value = (object?)value ?? DBNull.Value;
    }

    public override string[]? Parse(object value) => value == DBNull.Value ? null : (string[]?)value;
}

public class MangaTypeHandler : SqlMapper.TypeHandler<MangaType>
{
    public override void SetValue(IDbDataParameter parameter, MangaType value)
        => parameter.Value = value.ToString();
    public override MangaType Parse(object value)
        => Enum.Parse<MangaType>((string)value, ignoreCase: true);
}

public class UserRoleHandler : SqlMapper.TypeHandler<UserRole>
{
    public override void SetValue(IDbDataParameter parameter, UserRole value)
        => parameter.Value = value.ToString();
    public override UserRole Parse(object value)
        => Enum.Parse<UserRole>((string)value, ignoreCase: true);
}
