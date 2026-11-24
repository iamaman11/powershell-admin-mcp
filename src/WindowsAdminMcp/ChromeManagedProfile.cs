using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter<ChromeConnectionMode>))]
public enum ChromeConnectionMode { USER_BROWSER, MANAGED_BROWSER }

// A managed browser is explicitly started by the user with a dedicated data
// directory and Chrome's supported remote-debugging flags. This process does
// not launch an elevated browser or copy an existing user's cookies/profile.
internal static class ChromeManagedProfile
{
    internal static bool ValidId(string? id) => id is {Length: >= 1 and <= 40} &&
        id.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or
            >= '0' and <= '9' or '_' or '-');

    internal static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WindowsAdminMCP", "ChromeProfiles");

    internal static string? DataDirectory(string? id) =>
        ValidId(id) ? Path.Combine(Root, id!) : null;
}
