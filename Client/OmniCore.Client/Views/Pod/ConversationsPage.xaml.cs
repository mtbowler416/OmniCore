﻿using OmniCore.Client.ViewModels.Pods;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Xamarin.Forms;
using Xamarin.Forms.Xaml;

namespace OmniCore.Client.Views.Pod
{
    [XamlCompilation(XamlCompilationOptions.Compile)]
    public partial class ConversationsPage : ContentPage
    {
        public ConversationsPage()
        {
            InitializeComponent();
            //new ConversationsViewModel(this);
        }
    }
}