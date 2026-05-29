using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DemoProject
{
    [ObservableObject]
    public partial class MainViewModel
    {
        [ObservableProperty]
        private string _username;

        [RelayCommand]
        private void Login()
        {
            Console.WriteLine($"Logging in as {_username}...");
        }
    }
}