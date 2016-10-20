// Copyright (c) Microsoft. All rights reserved. 
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using FluentAssertions;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Its.Domain.Testing;
using NUnit.Framework;
using static Microsoft.Its.Domain.Sql.Tests.TestDatabases;

namespace Microsoft.Its.Domain.Sql.Tests
{
    [Category("Catchups")]
    [TestFixture]
    [UseSqlEventStore]
    public class RemainingCatchupTimeTests : EventStoreDbTest
    {
        [Test]
        public async Task If_events_have_been_processed_during_initial_replay_then_the_remaining_time_is_estimated_correctly()
        {
            //arrange
            IEnumerable<EventHandlerProgress> progress = null;
            Events.Write(10);
            var eventsProcessed = 0;
            var projector = CreateProjector(e =>
            {
                if (eventsProcessed == 5)
                {
                    progress = EventHandlerProgressCalculator.Calculate(() => ReadModelDbContext());
                }
                VirtualClock.Current.AdvanceBy(TimeSpan.FromSeconds(1));
                eventsProcessed++;
            });

            //act
            await RunCatchup(projector);
            progress.Single(p => p.Name == EventHandler.FullName(projector))
                    .TimeRemainingForCatchup
                    .Should()
                    .Be(TimeSpan.FromSeconds(5));
        }

        [Test]
        public async Task If_events_have_been_processed_after_initial_replay_then_the_remaining_time_is_estimated_correctly()
        {
            //arrange
            //Initial replay
            Events.Write(10);
            IEnumerable<EventHandlerProgress> progress = null;
            var eventsProcessed = 0;
            var projector = CreateProjector(e =>
            {
                if (eventsProcessed == 5)
                {
                    progress = EventHandlerProgressCalculator.Calculate(() => ReadModelDbContext());
                }
                VirtualClock.Current.AdvanceBy(TimeSpan.FromSeconds(1));
                eventsProcessed++;
            });
            await RunCatchup(projector);

            //new set of events come in
            Events.Write(10);

            //act
            await RunCatchup(projector);
            progress.Single(p => p.Name == EventHandler.FullName(projector))
                    .TimeRemainingForCatchup
                    .Should()
                    .Be(TimeSpan.FromSeconds(5));
        }

        [Test]
        public async Task If_events_have_been_processed_after_initial_replay_then_the_time_taken_for_initial_replay_is_saved()
        {
            //arrange
            var projector = CreateProjector(e =>
            {
                VirtualClock.Current.AdvanceBy(TimeSpan.FromSeconds(1));
            });

            //Initial replay
            Events.Write(10);
            await RunCatchup(projector);

            //new set of events come in
            Events.Write(5);

            //act
            await RunCatchup(projector);
            var progress = EventHandlerProgressCalculator.Calculate(() => ReadModelDbContext());

            //assert
            progress.Single(p => p.Name == EventHandler.FullName(projector))
                    .TimeTakenForInitialCatchup
                    .Should()
                    .Be(TimeSpan.FromSeconds(9));
        }

        [Test]
        public async Task If_events_have_been_processed_after_initial_replay_then_the_number_of_events_for_initial_replay_is_saved()
        {
            //arrange
            var projector = CreateProjector(e => VirtualClock.Current.AdvanceBy(TimeSpan.FromSeconds(1)));

            //Initial replay
            Events.Write(10);
            await RunCatchup(projector);

            //new set of events come in
            Events.Write(5);
            await RunCatchup(projector);

            //act
            var progress = EventHandlerProgressCalculator.Calculate(() => ReadModelDbContext());

            //assert
            progress.Single(p => p.Name == EventHandler.FullName(projector))
                    .InitialCatchupEvents
                    .Should()
                    .Be(10);
        }

        [Test]
        public async Task If_events_have_been_processed_then_the_correct_number_of_remaining_events_is_returned()
        {
            //arrange
            IEnumerable<EventHandlerProgress> progress = null;
            Events.Write(5);

            var eventsProcessed = 0;
            var projector = CreateProjector(e =>
            {
                if (eventsProcessed == 4)
                {
                    progress = EventHandlerProgressCalculator.Calculate(() => ReadModelDbContext());
                }
                eventsProcessed++;
            });

            //act
            await RunCatchup(projector);

            //assert
            progress.Single(p => p.Name == EventHandler.FullName(projector))
                    .EventsRemaining
                    .Should()
                    .Be(1);
        }

        [Test]
        public async Task If_all_events_have_been_processed_then_the_remaining_time_is_zero()
        {
            //arrange
            Events.Write(5);
            var projector = CreateProjector();
            await RunCatchup(projector);

            //act
            var progress = EventHandlerProgressCalculator.Calculate(() => ReadModelDbContext());

            //assert
            progress.Single(p => p.Name == EventHandler.FullName(projector))
                    .TimeRemainingForCatchup
                    .Should()
                    .Be(TimeSpan.FromMinutes(0));
        }

        [Test]
        public async Task If_all_events_have_been_processed_then_the_percentage_completed_is_100()
        {
            //arrange
            Events.Write(5);
            var projector = CreateProjector();
            await RunCatchup(projector);

            //act
            var progress = EventHandlerProgressCalculator.Calculate(() => ReadModelDbContext());

            //assert
            progress.Single(p => p.Name == EventHandler.FullName(projector))
                    .PercentageCompleted
                    .Should()
                    .Be(100);
        }

        private IUpdateProjectionWhen<IEvent> CreateProjector(
            Action<IEvent> action = null,
            [CallerMemberName] string callerMemberName = null)
        {
            return Projector.Create(action ?? (_ => { })).Named(callerMemberName);
        }

        private async Task RunCatchup(
            IUpdateProjectionWhen<IEvent> projector,
            [CallerMemberName] string projectorName = null)
        {
            using (var catchup = CreateReadModelCatchup(projector ??
                                                        CreateProjector(
                                                            e => { },
                                                            projectorName)))
            {
                await catchup.Run();
            }
        }
    }
}