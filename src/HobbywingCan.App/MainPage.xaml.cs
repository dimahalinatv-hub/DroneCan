using HobbywingCan.App.ViewModels;

namespace HobbywingCan.App;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
        BindingContext = new MainViewModel(Dispatcher)
        {
            Confirm = (title, message) => DisplayAlertAsync(title, message, "Write", "Cancel"),
            Alert = (title, message) => DisplayAlertAsync(title, message, "OK"),
        };
    }
}
