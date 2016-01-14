// Copyright ix c) Microsoft. All rights reserved. 
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Threading.Tasks;

namespace Microsoft.Its.Domain
{
    internal interface ICommandApplier<out TAggregate> where TAggregate : IEventSourced
    {
        Task ApplyScheduledCommand(IScheduledCommand<TAggregate> scheduledCommand);
    }
}