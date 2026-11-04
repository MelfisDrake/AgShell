namespace AgShell.Core.Tests
{
    public class InputSourceLockTests
    {
        private const string Us = "04090409";
        private const string Ru = "04190419";
        private static readonly HashSet<string> Enabled = new HashSet<string> { Us, Ru };

        [Fact]
        public void EngagingSelectsTheTargetAndRemembersTheEarlierLayout()
        {
            InputSourceLock lockState = new InputSourceLock();
            Assert.Equal(Us, lockState.Reconcile(true, Us, Enabled, Ru));
            Assert.Equal(Ru, lockState.Baseline);
        }

        [Fact]
        public void AManualSwitchWhileEngagedIsSwitchedBackAndTheBaselineKept()
        {
            InputSourceLock lockState = new InputSourceLock();
            lockState.Reconcile(true, Us, Enabled, Ru);
            Assert.Null(lockState.Reconcile(true, Us, Enabled, Us));
            Assert.Equal(Us, lockState.Reconcile(true, Us, Enabled, Ru));
            Assert.Equal(Ru, lockState.Baseline);
        }

        [Fact]
        public void LosingTerminalFocusRestoresTheEarlierLayoutOnce()
        {
            InputSourceLock lockState = new InputSourceLock();
            lockState.Reconcile(true, Us, Enabled, Ru);
            Assert.Equal(Ru, lockState.Reconcile(false, Us, Enabled, Us));
            Assert.Null(lockState.Baseline);
            Assert.Null(lockState.Reconcile(false, Us, Enabled, Ru));
        }

        [Fact]
        public void EngagingOnTheTargetItselfSelectsNothingEitherWay()
        {
            InputSourceLock lockState = new InputSourceLock();
            Assert.Null(lockState.Reconcile(true, Us, Enabled, Us));
            Assert.Equal(Us, lockState.Baseline);
            Assert.Null(lockState.Reconcile(false, Us, Enabled, Us));
        }

        [Fact]
        public void TurningTheSettingOffWhileEngagedRestores()
        {
            InputSourceLock lockState = new InputSourceLock();
            lockState.Reconcile(true, Us, Enabled, Ru);
            Assert.Equal(Ru, lockState.Reconcile(true, null, Enabled, Us));
            Assert.Null(lockState.Baseline);
        }

        [Fact]
        public void ChangingTheTargetWhileEngagedKeepsTheOriginalBaseline()
        {
            const string De = "04070407";
            InputSourceLock lockState = new InputSourceLock();
            HashSet<string> enabled = new HashSet<string>(Enabled) { De };
            lockState.Reconcile(true, Us, enabled, Ru);
            Assert.Equal(De, lockState.Reconcile(true, De, enabled, Us));
            Assert.Equal(Ru, lockState.Baseline);
        }

        [Fact]
        public void ATargetWindowsCannotSelectEngagesNothing()
        {
            InputSourceLock lockState = new InputSourceLock();
            Assert.Null(lockState.Reconcile(true, "04070407", Enabled, Ru));
            Assert.Null(lockState.Baseline);
        }

        [Fact]
        public void ATargetRemovedWhileEngagedReleasesTheOverride()
        {
            InputSourceLock lockState = new InputSourceLock();
            lockState.Reconcile(true, Us, Enabled, Ru);
            Assert.Equal(Ru, lockState.Reconcile(true, Us, new HashSet<string> { Ru }, Us));
            Assert.Null(lockState.Baseline);
        }

        [Fact]
        public void ARefusedSelectionLeavesNothingToRestore()
        {
            InputSourceLock lockState = new InputSourceLock();
            lockState.Reconcile(true, Us, Enabled, Ru);
            lockState.SelectionFailed();
            Assert.Null(lockState.Release(Ru));
        }
    }
}
