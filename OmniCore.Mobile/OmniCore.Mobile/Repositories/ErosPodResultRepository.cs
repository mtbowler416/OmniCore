﻿using OmniCore.Impl.Eros;
using OmniCore.Model.Interfaces;
using System;
using System.Collections.Generic;
using System.Text;

namespace OmniCore.Mobile.Repositories
{
    class ErosPodResultRepository : SqliteRepository<ErosResult>, IPodResultRepository<ErosResult>
    {
    }
}