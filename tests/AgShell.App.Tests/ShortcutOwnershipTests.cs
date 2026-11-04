using System.Text.Json;

namespace AgShell.App.Tests
{
    public class ShortcutOwnershipTests
    {
        [Fact]
        public void ReturningAfterReleaseElsewhereDropsStaleOwnership()
        {
            JsonElement snapshot = Snapshot(false);
            JsonElement result = WindowLibrary.ReconcileShortcutOwnership(snapshot, code => false);
            Assert.Empty(result.GetProperty("consumed").EnumerateArray());
            Assert.Empty(result.GetProperty("menuOwned").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, result.GetProperty("heldRepeatKey").ValueKind);
            Assert.Equal(JsonValueKind.Null, result.GetProperty("repeatPrefix").ValueKind);
        }

        [Fact]
        public void AStillHeldTailSurvivesAWindowTransfer()
        {
            JsonElement snapshot = Snapshot(true);
            JsonElement result = WindowLibrary.ReconcileShortcutOwnership(snapshot, code => true);
            Assert.Equal(snapshot.GetRawText(), result.GetRawText());
            Assert.Equal("KeyR", result.GetProperty("heldRepeatKey").GetString());
        }

        [Fact]
        public void ALostReleaseStartsTheRepeatDeadlineInsteadOfLeavingATailHeld()
        {
            long before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            JsonElement result = WindowLibrary.ReconcileShortcutOwnership(Snapshot(true), code => false);
            Assert.Equal(JsonValueKind.Null, result.GetProperty("heldRepeatKey").ValueKind);
            Assert.Empty(result.GetProperty("consumed").EnumerateArray());
            Assert.Equal(JsonValueKind.Array, result.GetProperty("repeatPrefix").ValueKind);
            Assert.InRange(result.GetProperty("deadline").GetDouble(), before + 500, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 500);
        }

        [Theory]
        [InlineData("", "renderer", true, "timer", true)]
        [InlineData("", "renderer", false, "timer", false)]
        [InlineData("other", "renderer", true, "timer", false)]
        [InlineData("renderer", "renderer", false, "timer", true)]
        [InlineData("other", "renderer", true, "key", true)]
        public void OnlyTheActiveRendererCanTakeAnUnownedTimer(string owner, string origin, bool active, string reason, bool expected)
        {
            Assert.Equal(expected, WindowLibrary.CanPublishShortcut(owner, origin, active, reason));
        }

        private static JsonElement Snapshot(bool repeating)
        {
            return JsonSerializer.SerializeToElement(new
            {
                pending = Array.Empty<object>(), repeatPrefix = repeating ? new[] { new { code = "KeyL", ctrl = true, alt = true } } : null,
                heldRepeatKey = repeating ? "KeyR" : null, consumed = new[] { "KeyR" }, menuOwned = new[] { "KeyR" }, deadline = (double?)null,
            });
        }
    }
}
