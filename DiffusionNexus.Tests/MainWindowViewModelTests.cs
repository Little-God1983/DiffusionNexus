using DiffusionNexus.UI.ViewModels;
using Xunit;

namespace DiffusionNexus.Tests
{
    public class MainWindowViewModelTests
    {
        [Fact]
        public void ToggleMenuCommand_UpdatesLayout()
        {
            var vm = new MainWindowViewModel();
            var initialWidth = vm.SidebarWidth;
            vm.ToggleMenuCommand.Execute().Subscribe();
            Assert.NotEqual(initialWidth, vm.SidebarWidth);
        }
    }
}
