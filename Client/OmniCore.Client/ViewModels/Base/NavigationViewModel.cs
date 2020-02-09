﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using System.Threading.Tasks;
using OmniCore.Model.Interfaces.Client;
using Xamarin.Forms;

namespace OmniCore.Client.ViewModels.Base
{
    public abstract class NavigationViewModel : BaseViewModel
    {
        public bool CanGoForwards { get; set; }

        public bool CanGoBackwards { get; set; }

        public Page PreviousPage { get; set; }

        //protected ContentPage RootPage { get; set; }
        //public override Task OnInitialize()
        //{
        //    return Task.CompletedTask;
        //}

        //public override Task OnDispose()
        //{
        //    return Task.CompletedTask;
        //}

        //protected NavigationViewModel(ICoreClient client) : base(client)
        //{
        //}
        protected NavigationViewModel(ICoreClient client) : base(client)
        {
        }

        public abstract Task<Page> GetNextPage();
    }
}
