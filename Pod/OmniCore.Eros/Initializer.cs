﻿using OmniCore.Model.Interfaces.Base;
using OmniCore.Model.Interfaces.Common;
using OmniCore.Model.Interfaces.Services.Facade;
using OmniCore.Model.Interfaces.Services.Internal;

namespace OmniCore.Eros
{
    public static class Initializer
    {
        public static ICoreContainer<IServerResolvable> WithOmnipodEros
            (this ICoreContainer<IServerResolvable> container)
        {
            return container
                .One<IErosPodProvider, ErosPodProvider>()
                .Many<ErosPod>()
                .Many<IPodRequest, ErosPodRequest>()
                .Many<ITaskQueue, ErosTaskQueue>();
        }
    }
}

