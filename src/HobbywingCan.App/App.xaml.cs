using Microsoft.Extensions.DependencyInjection;

namespace HobbywingCan.App;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		// fit the window to the screen: with display scaling 1500x900 device-independent units can exceed it
		var display = DeviceDisplay.Current.MainDisplayInfo;
		double screenWidth = display.Width / display.Density;
		double screenHeight = display.Height / display.Density;
		double width = Math.Min(1500, screenWidth - 40);
		double height = Math.Min(900, screenHeight - 80);
		return new Window(new AppShell())
		{
			Title = "Hobbywing CAN Monitor",
			Width = width,
			Height = height,
			X = Math.Max(0, (screenWidth - width) / 2),
			Y = Math.Max(0, (screenHeight - height) / 2 - 20),
		};
	}
}