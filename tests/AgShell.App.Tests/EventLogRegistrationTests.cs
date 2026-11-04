using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using AgShell.App;
using Xunit.Abstractions;

namespace AgShell.App.Tests
{
    public class EventLogRegistrationTests
    {
        private readonly ITestOutputHelper output;

        public EventLogRegistrationTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Theory]
        [InlineData("resource.dll", false)]
        [InlineData("resource.dll", true)]
        [InlineData("event's & $resource.dll", false)]
        [InlineData("event's & $resource.dll", true)]
        public void RegistrationMakesOnlyTheResourceReadableByTheEventLogService(string name, bool installer)
        {
            string directory = Path.Combine(Path.GetTempPath(), "agshell-eventlog-access-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string resource = Path.Combine(directory, name);
            string neighbour = Path.Combine(directory, "other.txt");
            SecurityIdentifier service = new SecurityIdentifier(EventLogRegistration.ServiceSid);
            try
            {
                File.WriteAllText(resource, "resource bytes");
                File.WriteAllText(neighbour, "other bytes");
                EventLogAclDiagnostics diagnostics = new EventLogAclDiagnostics(output, directory);
                diagnostics.Environment(resource);
                diagnostics.Persisted("before-parent", directory);
                diagnostics.Persisted("before-sibling", neighbour);
                byte[] before = diagnostics.Persisted("before-resource", resource);
                string directoryBefore = new DirectoryInfo(directory).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
                string neighbourBefore = new FileInfo(neighbour).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
                FileSecurity original = new FileInfo(resource).GetAccessControl();
                string[] resourceBefore = Rules(original);
                diagnostics.Managed("before-managed-resource", original);
                Assert.Equal(service, new NTAccount(@"NT SERVICE\EventLog").Translate(typeof(SecurityIdentifier)));
                int exit = Run(ResourceAccessScript(resource, installer), installer);
                byte[] persisted = diagnostics.Persisted("after-grant-resource", resource);
                diagnostics.Persisted("after-grant-parent", directory);
                diagnostics.Persisted("after-grant-sibling", neighbour);
                output.WriteLine("native resource descriptor changed by permission script: " + !before.SequenceEqual(persisted));
                Assert.Equal(0, exit);
                Assert.Equal("resource bytes", File.ReadAllText(resource));
                Assert.Equal(directoryBefore, new DirectoryInfo(directory).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
                Assert.Equal(neighbourBefore, new FileInfo(neighbour).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
                FileSecurity changed = new FileInfo(resource).GetAccessControl();
                diagnostics.Managed("after-grant-managed-resource", changed);
                FileSystemAccessRule rule = Assert.Single(changed.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(), item => item.IdentityReference.Equals(service));
                Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
                Assert.Equal(FileSystemRights.Read, rule.FileSystemRights & FileSystemRights.Read);
                Assert.Equal((FileSystemRights)0, rule.FileSystemRights & (FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.ExecuteFile | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership));
                changed.RemoveAccessRuleSpecific(rule);
                diagnostics.Managed("after-remove-in-memory-only", changed);
                byte[] afterEdit = diagnostics.Persisted("after-remove-fresh-resource", resource);
                Assert.Equal(persisted, afterEdit);
                Assert.Equal(original.AreAccessRulesProtected, changed.AreAccessRulesProtected);
                Assert.Equal(resourceBefore, Rules(changed));
            }
            finally
            {
                Directory.Delete(directory, true);
                output.WriteLine("owned fixture removed=" + !Directory.Exists(directory));
            }
        }

        [Fact]
        public void RemovingServiceRuleInMemoryDoesNotPersistTheEdit()
        {
            string directory = Path.Combine(Path.GetTempPath(), "agshell-eventlog-control-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string resource = Path.Combine(directory, "control.dll");
            try
            {
                File.WriteAllText(resource, "control bytes");
                SecurityIdentifier service = new SecurityIdentifier(EventLogRegistration.ServiceSid);
                FileInfo file = new FileInfo(resource);
                FileSecurity setup = file.GetAccessControl();
                setup.AddAccessRule(new FileSystemAccessRule(service, FileSystemRights.Read, AccessControlType.Allow));
                file.SetAccessControl(setup);
                EventLogAclDiagnostics diagnostics = new EventLogAclDiagnostics(output, directory);
                byte[] before = diagnostics.Persisted("control-before-remove", resource);
                FileSecurity edit = file.GetAccessControl();
                FileSystemAccessRule rule = Assert.Single(edit.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(), item => item.IdentityReference.Equals(service));
                edit.RemoveAccessRuleSpecific(rule);
                diagnostics.Managed("control-removed-in-memory", edit);
                Assert.DoesNotContain(edit.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(), item => item.IdentityReference.Equals(service));
                Assert.Equal(before, diagnostics.Persisted("control-fresh-after-remove", resource));
                Assert.Single(file.GetAccessControl().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(), item => item.IdentityReference.Equals(service));
            }
            finally
            {
                Directory.Delete(directory, true);
                output.WriteLine("owned fixture removed=" + !Directory.Exists(directory));
            }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void RegistrationPreservesExplicitRulesProtectionAndOwnership(bool protect, bool installer)
        {
            string directory = Path.Combine(Path.GetTempPath(), "agshell-eventlog-preserve-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string resource = Path.Combine(directory, "resource'[1]&$.dll");
            try
            {
                File.WriteAllText(resource, "resource bytes");
                SecurityIdentifier other = new SecurityIdentifier("S-1-5-21-111111111-222222222-333333333-4001");
                SecurityIdentifier service = new SecurityIdentifier(EventLogRegistration.ServiceSid);
                FileInfo file = new FileInfo(resource);
                FileSecurity setup = file.GetAccessControl();
                setup.AddAccessRule(new FileSystemAccessRule(other, FileSystemRights.ReadAttributes, AccessControlType.Allow));
                setup.AddAccessRule(new FileSystemAccessRule(other, FileSystemRights.WriteData, AccessControlType.Deny));
                setup.SetAccessRuleProtection(protect, true);
                file.SetAccessControl(setup);
                FileSecurity original = file.GetAccessControl();
                string[] originalRules = Rules(original);
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    Assert.Equal(0, Run(ResourceAccessScript(resource, installer), installer));
                    FileSecurity changed = file.GetAccessControl();
                    Assert.Equal(originalRules, Rules(changed, service));
                    Assert.Equal(protect, changed.AreAccessRulesProtected);
                    Assert.Equal(original.GetOwner(typeof(SecurityIdentifier)), changed.GetOwner(typeof(SecurityIdentifier)));
                    Assert.Equal(original.GetGroup(typeof(SecurityIdentifier)), changed.GetGroup(typeof(SecurityIdentifier)));
                    FileSystemAccessRule rule = Assert.Single(changed.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(), item => item.IdentityReference.Equals(service));
                    Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
                    Assert.Equal(FileSystemRights.Read | FileSystemRights.Synchronize, rule.FileSystemRights);
                    Assert.Equal(InheritanceFlags.None, rule.InheritanceFlags);
                    Assert.Equal(PropagationFlags.None, rule.PropagationFlags);
                    Assert.Equal("resource bytes", File.ReadAllText(resource));
                }
            }
            finally
            {
                Directory.Delete(directory, true);
                output.WriteLine("owned fixture removed=" + !Directory.Exists(directory));
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ResourcePermissionsStillFollowParentRevocation(bool installer)
        {
            string directory = Path.Combine(Path.GetTempPath(), "agshell-eventlog-inheritance-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string resource = Path.Combine(directory, "resource.dll");
            string sibling = Path.Combine(directory, "other.txt");
            try
            {
                SecurityIdentifier inherited = new SecurityIdentifier("S-1-5-21-111111111-222222222-333333333-4001");
                SecurityIdentifier service = new SecurityIdentifier(EventLogRegistration.ServiceSid);
                DirectoryInfo parent = new DirectoryInfo(directory);
                DirectorySecurity setup = parent.GetAccessControl();
                FileSystemAccessRule grant = new FileSystemAccessRule(inherited, FileSystemRights.Read,
                    InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow);
                setup.AddAccessRule(grant);
                parent.SetAccessControl(setup);
                File.WriteAllText(resource, "resource bytes");
                File.WriteAllText(sibling, "sibling bytes");
                Assert.Single(new FileInfo(resource).GetAccessControl().GetAccessRules(false, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(), item => item.IdentityReference.Equals(inherited));
                Assert.Equal(0, Run(ResourceAccessScript(resource, installer), installer));
                DirectorySecurity revoke = parent.GetAccessControl();
                revoke.RemoveAccessRuleSpecific(grant);
                parent.SetAccessControl(revoke);
                FileSecurity changed = new FileInfo(resource).GetAccessControl();
                Assert.DoesNotContain(changed.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(), item => item.IdentityReference.Equals(inherited));
                Assert.DoesNotContain(new FileInfo(sibling).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(), item => item.IdentityReference.Equals(inherited) || item.IdentityReference.Equals(service));
                FileSystemAccessRule serviceRule = Assert.Single(changed.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(), item => item.IdentityReference.Equals(service));
                Assert.False(serviceRule.IsInherited);
                Assert.Equal(FileSystemRights.Read | FileSystemRights.Synchronize, serviceRule.FileSystemRights);
                Assert.Equal("resource bytes", File.ReadAllText(resource));
                Assert.Equal("sibling bytes", File.ReadAllText(sibling));
            }
            finally
            {
                Directory.Delete(directory, true);
                output.WriteLine("owned fixture removed=" + !Directory.Exists(directory));
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void MissingResourceStopsBeforeChangingProviderRegistration(bool installer)
        {
            string directory = Path.Combine(Path.GetTempPath(), "agshell-eventlog-missing-" + Guid.NewGuid().ToString("N"));
            string sentinel = Path.Combine(Path.GetTempPath(), "agshell-eventlog-sentinel-" + Guid.NewGuid().ToString("N"));
            try
            {
                string script = ResourceAccessScript(Path.Combine(directory, "absent.dll"), installer)
                    + "[IO.File]::WriteAllText('" + sentinel.Replace("'", "''") + "', 'registration reached')";
                Assert.NotEqual(0, Run(script, installer));
                Assert.False(File.Exists(sentinel));
            }
            finally
            {
                File.Delete(sentinel);
            }
        }

        private static string ResourceAccessScript(string resource, bool installer)
        {
            if (!installer)
            {
                return EventLogRegistration.ResourceAccessScript(resource);
            }
            string root = AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(root, "agshell.slnx")))
            {
                root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("agshell.slnx not found above the tests");
            }
            string source = File.ReadAllText(Path.Combine(root, "installer", "agshell.iss"));
            Match function = Regex.Match(source, @"function EventLogResourceAccessScript\(Resources: String\): String;\s*begin\s*Result := (?<expression>.*?)\s*StringChangeEx\(Result, '__RESOURCE__', PowerShellLiteral\(Resources\), True\);\s*end;", RegexOptions.Singleline);
            Assert.True(function.Success, "Installer ACL script function not found");
            string template = string.Concat(Regex.Matches(function.Groups["expression"].Value, "'((?:[^']|'')*)'").Cast<Match>()
                .Select(match => match.Groups[1].Value.Replace("''", "'")));
            string script = template.Replace("__RESOURCE__", "'" + resource.Replace("'", "''") + "'");
            Assert.Equal(EventLogRegistration.ResourceAccessScript(resource), script);
            Assert.Contains("Script := EventLogResourceAccessScript(Resources) +", source);
            Assert.Contains("'-NoProfile -NonInteractive -Command \"' + Script + '\"'", source);
            return script;
        }

        private int Run(string script, bool installer = false)
        {
            script = "Write-Output ('powershell=' + $PSVersionTable.PSVersion.ToString()); "
                + "Write-Output ('icacls-resolves-to-system=' + ((Get-Command icacls.exe).Source -eq (Join-Path $env:SystemRoot 'System32\\icacls.exe'))); "
                + script;
            ProcessStartInfo start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add(installer ? "-Command" : "-EncodedCommand");
            start.ArgumentList.Add(installer ? script : Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            output.WriteLine("entrypoint=" + (installer ? "installer -Command" : "app -EncodedCommand"));
            using Process process = Process.Start(start)!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            Assert.True(process.WaitForExit(15000), "Resource permission command timed out");
            Task.WaitAll(stdout, error);
            output.WriteLine("permission command exit=" + process.ExitCode);
            output.WriteLine("stdout=" + stdout.Result);
            output.WriteLine("stderr=" + error.Result);
            return process.ExitCode;
        }

        private static string[] Rules(FileSecurity security, SecurityIdentifier? except = null)
        {
            return security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Where(rule => !rule.IdentityReference.Equals(except))
                .Select(rule => rule.IdentityReference.Value + " " + rule.AccessControlType + " " + rule.FileSystemRights + " " + rule.InheritanceFlags + " " + rule.PropagationFlags + " " + rule.IsInherited)
                .ToArray();
        }
    }
}
