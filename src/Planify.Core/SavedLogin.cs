using System.Security.Cryptography;
using System.Text;

namespace Planify.Core;

public sealed record LoginAccount(string Server, string User)
{
    public string CredentialKey
    {
        get
        {
            var uri = new Uri(Server.Trim());
            if (uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new ArgumentException("请输入不含账号或查询参数的 HTTPS 服务器地址。");
            if (string.IsNullOrWhiteSpace(User)) throw new ArgumentException("请输入用户名。");
            string identity = uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "\n" + User.Trim();
            return "PlanifyWindowsCommunity:CalDAV:v1:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        }
    }
}

public interface ICredentialStore
{
    bool Contains(LoginAccount account);
    string? Read(LoginAccount account);
    void Save(LoginAccount account, string password);
    void Remove(LoginAccount account);
}

public sealed record LoginResult<T>(T Value, bool PreferenceApplied);

// Authentication must finish before saving or removing a credential. A failed vault
// operation cannot turn a successful network login into a discarded connection.
public sealed class SavedLogin(ICredentialStore credentials)
{
    public async Task<LoginResult<T>> Connect<T>(LoginAccount account, string enteredPassword, bool remember, Func<string, Task<T>> authenticate)
    {
        string? secret = enteredPassword.Length > 0 ? enteredPassword : credentials.Read(account);
        if (string.IsNullOrEmpty(secret)) throw new InvalidOperationException("请输入应用专用密码。");
        T value = await authenticate(secret);
        try
        {
            if (remember) credentials.Save(account, secret); else credentials.Remove(account);
            return new(value, true);
        }
        catch (Exception) { return new(value, false); }
    }
    public async Task<T?> Restore<T>(LoginAccount account, Func<string, Task<T>> authenticate) where T : class
    {
        string? secret = credentials.Read(account);
        return string.IsNullOrEmpty(secret) ? null : await authenticate(secret);
    }
}
