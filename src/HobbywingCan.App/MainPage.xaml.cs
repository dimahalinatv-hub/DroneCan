using HobbywingCan.App.ViewModels;
using static HobbywingCan.App.Localization.Localizer;

namespace HobbywingCan.App;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
        BindingContext = new MainViewModel(Dispatcher)
        {
            Confirm = (title, message) => DisplayAlertAsync(title, message, Tr("Ui_Write"), Tr("Ui_Cancel")),
            Alert = (title, message) => DisplayAlertAsync(title, message, "OK"),
            Ask = (title, message, accept) => DisplayAlertAsync(title, message, accept, Tr("Ui_Cancel")),
        };
    }
}
