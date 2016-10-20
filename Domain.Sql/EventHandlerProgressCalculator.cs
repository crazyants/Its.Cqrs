// Copyright (c) Microsoft. All rights reserved. 
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;

namespace Microsoft.Its.Domain.Sql
{
    internal static class EventHandlerProgressCalculator
    {
        public static IEnumerable<EventHandlerProgress> Calculate(
            Func<DbContext> createReadModelDbContext,
            Func<EventStoreDbContext> createEventStoreDbContext = null)
        {
            if (createReadModelDbContext == null)
            {
                throw new ArgumentNullException(nameof(createReadModelDbContext));
            }

            createEventStoreDbContext = createEventStoreDbContext ??
                                        (() => Configuration.Current.EventStoreDbContext());

            int eventStoreCount;

            using (var db = createEventStoreDbContext())
            {
                eventStoreCount = db.Events.Count();
            }

            if (eventStoreCount == 0)
            {
                return Enumerable.Empty<EventHandlerProgress>();
            }

            var now = Clock.Now();
            var progress = new List<EventHandlerProgress>();

            ReadModelInfo[] readModelInfos;

            using (var db = createReadModelDbContext())
            {
                readModelInfos = db.Set<ReadModelInfo>().ToArray();
            }

            readModelInfos
                .ForEach(i =>
                {
                    var eventsProcessed = i.InitialCatchupEndTime.HasValue
                                              ? i.BatchTotalEvents - i.BatchRemainingEvents
                                              : i.InitialCatchupEvents - i.BatchRemainingEvents;

                    if (eventsProcessed == 0)
                    {
                        return;
                    }

                    long? timeTakenForProcessedEvents = null;
                    if (i.BatchStartTime.HasValue && i.InitialCatchupStartTime.HasValue)
                    {
                        timeTakenForProcessedEvents = i.InitialCatchupEndTime.HasValue
                                                          ? (now - i.BatchStartTime).Value.Ticks
                                                          : (now - i.InitialCatchupStartTime).Value.Ticks;
                    }

                    var eventHandlerProgress = new EventHandlerProgress
                    {
                        Name = i.Name,
                        InitialCatchupEvents = i.InitialCatchupEvents,
                        TimeTakenForInitialCatchup = i.InitialCatchupStartTime.HasValue
                                                         ? (i.InitialCatchupEndTime ?? now) - i.InitialCatchupStartTime
                                                         : null,
                        TimeRemainingForCatchup = timeTakenForProcessedEvents.HasValue
                                                      ? (TimeSpan?) TimeSpan.FromTicks((long) (timeTakenForProcessedEvents*(i.BatchRemainingEvents/(decimal) eventsProcessed)))
                                                      : null,
                        EventsRemaining = i.BatchRemainingEvents,
                        PercentageCompleted = (1 - (decimal) i.BatchRemainingEvents/eventStoreCount)*100,
                        LatencyInMilliseconds = i.LatencyInMilliseconds,
                        LastUpdated = i.LastUpdated,
                        CurrentAsOfEventId = i.CurrentAsOfEventId,
                        FailedOnEventId = i.FailedOnEventId,
                        Error = i.Error
                    };

                    progress.Add(eventHandlerProgress);
                });
            return progress;
        }
    }
}