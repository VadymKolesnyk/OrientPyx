namespace OrientPyx.BusinessLogic.Services.Iof;

/// <summary>
/// «Прізвище Ім'я [По батькові]» as the IOF <c>Family</c> / <c>Given</c> pair (both required by the schema): the family
/// is the first word, the given name the rest (blank for a one-word name). The exact inverse of the EntryList import,
/// which joins <c>Family + " " + Given</c>.
/// </summary>
internal static class IofNames
{
    public static (string Family, string Given) Split(string? fullName)
    {
        var name = (fullName ?? string.Empty).Trim();
        var space = name.IndexOf(' ');
        return space < 0
            ? (name, string.Empty)
            : (name[..space], name[(space + 1)..].Trim());
    }
}
