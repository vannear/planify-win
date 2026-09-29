using Planify.App;
using Planify.Core;

// Only unique dummy test identities are used; never enumerate the user's vault.
if (args.Length != 2 || !Guid.TryParse(args[1], out _)) throw new ArgumentException("Usage: seed|verify|cleanup <unique test UUID>");
var account = new LoginAccount("https://planify-credential-test.invalid/" + args[1], "test-only");
var otherAccount = new LoginAccount(account.Server, "different-user");
var vault = new WindowsCredentialStore();
if (args[0] == "seed")
{
    if (vault.Contains(account)) throw new Exception("Test identity already exists");
    vault.Save(account, "dummy-test-value");
    Console.WriteLine("PASS native vault write");
}
else if (args[0] == "verify")
{
    try
    {
        if (vault.Read(account) != "dummy-test-value") throw new Exception("Cross-process retrieval failed");
        Console.WriteLine("PASS native vault read across process restart");
        if (vault.Read(otherAccount) != null) throw new Exception("Account isolation failed");
        Console.WriteLine("PASS account isolation");
        vault.Save(account, "dummy-updated-value");
        if (vault.Read(account) != "dummy-updated-value") throw new Exception("Update failed");
        Console.WriteLine("PASS native vault replace");
    }
    finally { vault.Remove(account); }
    if (vault.Read(account) != null) throw new Exception("Deletion failed");
    vault.Remove(account);
    Console.WriteLine("PASS native vault delete and repeated delete");
}
else if (args[0] == "cleanup") vault.Remove(account);
else throw new ArgumentException("Unknown test action");
