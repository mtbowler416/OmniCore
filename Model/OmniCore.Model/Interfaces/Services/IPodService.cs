﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OmniCore.Model.Interfaces.Base;
using OmniCore.Model.Interfaces.Services.Facade;
using OmniCore.Model.Interfaces.Services.Internal;
using IUser = OmniCore.Model.Interfaces.Services.Facade.IUser;

namespace OmniCore.Model.Interfaces.Services
{
    public interface IPodService : ICoreService
    {
        Task<IList<IPod>> ActivePods(CancellationToken cancellationToken);
        Task<IList<IPod>> ArchivedPods(CancellationToken cancellationToken);
        Task<IPodEros> NewErosPod(IUser user, IMedication medication, CancellationToken cancellationToken);
        IObservable<IErosRadio> ListErosRadios();
        Task<IPodDash> NewDashPod(IUser user, IMedication medication, CancellationToken cancellationToken);
    }
}