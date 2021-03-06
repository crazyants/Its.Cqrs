// Copyright (c) Microsoft. All rights reserved. 
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Its.Recipes;

namespace Microsoft.Its.Domain
{
    internal class DynamicProjector :
        IEventHandlerBinder,
        IEventHandler,
        INamedEventHandler,
        IEventQuery
    {
        private readonly Action<dynamic> onEvent;
        private readonly MatchEvent[] matchEvents;

        public DynamicProjector(Action<dynamic> onEvent, params MatchEvent[] matchEvents)
        {
            if (onEvent == null)
            {
                throw new ArgumentNullException(nameof(onEvent));
            }
            this.onEvent = onEvent;

            this.matchEvents = matchEvents.OrEmpty().Any()
                                   ? matchEvents
                                   : MatchEvent.All;
        }

        public IEnumerable<IEventHandlerBinder> GetBinders()
        {
            return new IEventHandlerBinder[]
            {
                this
            };
        }

        public string Name { get; set; }

        public Type EventType => typeof (IEvent);

        public IDisposable SubscribeToBus(object handler, IEventBus bus) =>
            bus.Events<IEvent>()
               .SubscribeDurablyAndPublishErrors(this,
                                                 e =>
                                                 {
                                                     if (matchEvents.Any(me => me.Matches(e)))
                                                     {
                                                         onEvent(e);
                                                     }
                                                 },
                                                 bus);

        public IEnumerable<MatchEvent> IncludedEventTypes => matchEvents;
    }
}