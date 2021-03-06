// Copyright (c) Microsoft. All rights reserved. 
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using FluentAssertions;
using Microsoft.Its.Domain.Testing;
using Microsoft.Its.Recipes;
using NUnit.Framework;
using System;
using System.Reactive.Disposables;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Its.Domain.Sql;
using Test.Domain.Ordering;

namespace Microsoft.Its.Domain.Tests
{
    [TestFixture]
    [DisableCommandAuthorization]
    public abstract class ReservationServiceTests
    {
        [Test]
        public void When_a_command_reserves_a_unique_value_then_a_subsequent_request_by_the_same_owner_succeeds()
        {
            // arrange
            var username = Any.CamelCaseName(5);

            // act
            var account1 = new CustomerAccount();
            var principal = new Customer
            {
                Name = Any.CamelCaseName()
            };
            account1.Apply(new RequestUserName
            {
                UserName = username,
                Principal = principal
            });
            account1.ConfirmSave();

            var account2 = new CustomerAccount();
            var secondAttempt = account2.Validate(new RequestUserName
            {
                UserName = username,
                Principal = principal
            });

            // assert
            secondAttempt.ShouldBeValid();
        }

        [Test]
        public void When_a_command_reserves_a_unique_value_then_a_subsequent_request_by_a_different_owner_fails()
        {
            // arrange
            var username = Any.CamelCaseName(5);

            // act
            var account1 = new CustomerAccount();
            account1.Apply(new RequestUserName
            {
                UserName = username,
                Principal = new Customer
                {
                    Name = Any.CamelCaseName()
                }
            });
            account1.ConfirmSave();

            var account2 = new CustomerAccount();
            var secondPrincipal = new Customer
            {
                Name = Any.CamelCaseName()
            };
            var secondAttempt = account2.Validate(new RequestUserName
            {
                UserName = username,
                Principal = secondPrincipal
            });

            // assert
            secondAttempt.ShouldBeInvalid($"The user name {username} is taken. Please choose another.");
        }

        [Test]
        public async Task Only_one_of_multiple_concurrent_attempts_to_reserve_the_same_value_can_succeed()
        {
            var username = Any.CamelCaseName();
            var scope = Any.CamelCaseName();
            var barrier = new Barrier(2);

            // onSave = () => barrier.SignalAndWait(2000);
            var reservationService = Configuration.Current.ReservationService();

            var attempt1 = Task.Run(async () =>
            {
                barrier.SignalAndWait(1000);
                return await reservationService.Reserve(username, scope, "owner-token1");
            });
            var attempt2 = Task.Run(async () =>
            {
                barrier.SignalAndWait(1000);
                return await reservationService.Reserve(username, scope, "owner-token2");
            });

            await Task.WhenAll(attempt1, attempt2);

            attempt1.Result.Should().Be(!attempt2.Result);
        }

        [Test]
        public async Task When_a_value_is_confirmed_then_it_can_be_re_reserved_using_the_same_owner_token_because_idempotency()
        {
            // arrange
            var value = Any.FullName();
            var ownerToken = Any.Guid().ToString();
            var scope = "default-scope";
            var reservationService = Configuration.Current.ReservationService();
            await reservationService.Reserve(value, scope, ownerToken);
            await reservationService.Confirm(value, scope, ownerToken);

            // act
            var succeeded = await reservationService.Reserve(value, scope, ownerToken);

            // assert
            succeeded.Should().BeTrue("because idempotency");
        }

        [Test]
        public async Task When_a_value_is_confirmed_then_an_attempt_using_a_different_owner_token_to_reserve_it_again_throws()
        {
            // arrange
            var username = Any.Email();
            var ownerToken = Any.Email();
            var scope = "default-scope";
            var reservationService = Configuration.Current.ReservationService();
            await reservationService.Reserve(username, scope, ownerToken);
            await reservationService.Confirm(username, scope, ownerToken);

            // act
            var succeeded = await reservationService.Reserve(username, scope, ownerToken + "!");

            // assert
            succeeded.Should().BeFalse();
        }

        [Test]
        public async Task A_reservation_cannot_be_confirmed_using_the_wrong_owner_token()
        {
            // arrange
            var value = Any.FullName();
            var ownerToken = Any.Guid().ToString();
            var scope = "default-scope";
            var reservationService = Configuration.Current.ReservationService();
            await reservationService.Reserve(value, scope, ownerToken, TimeSpan.FromMinutes(5));

            // act
            var wrongOwnerToken = Any.Guid();
            var confirmed = reservationService.Confirm(value, scope, wrongOwnerToken.ToString()).Result;

            // assert
            confirmed.Should().BeFalse();
        }

        [Test]
        public async Task When_Confirm_is_called_for_a_nonexistent_reservation_then_it_returns_false()
        {
            var reservationService = Configuration.Current.ReservationService();

            var value = await reservationService.Confirm(Any.CamelCaseName(), Any.CamelCaseName(), Any.CamelCaseName());

            value.Should().BeFalse();
        }

        [Test]
        public async Task A_reservation_can_be_cancelled_using_its_owner_token()
        {
            // arrange
            var value = Any.FullName();
            var ownerToken = Any.Guid().ToString();
            var scope = "default-scope";
            var reservationService = Configuration.Current.ReservationService();
            await reservationService.Reserve(value, scope, ownerToken, TimeSpan.FromMinutes(5));

            // act
            await reservationService.Cancel(
                value: value,
                scope: scope,
                ownerToken: ownerToken);

            // assert
            // check that someone else can now reserve the same value
            var succeeded = await reservationService.Reserve(value, scope, Any.Guid().ToString(), TimeSpan.FromMinutes(5));

            succeeded.Should().BeTrue();
        }

        [Test]
        public async Task An_attempt_to_cancel_a_reservation_without_the_correct_owner_token_fails()
        {
            // arrange
            var value = Any.FullName();
            var ownerToken = Any.Guid().ToString();
            var scope = "default-scope";
            var reservationService = Configuration.Current.ReservationService();
            await reservationService.Reserve(value, scope, ownerToken, TimeSpan.FromMinutes(5));

            // act
            var wrongOwnerToken = Any.Guid().ToString();
            var cancelled = reservationService.Cancel(value, scope, wrongOwnerToken).Result;

            // assert
            cancelled.Should().BeFalse();
        }

        [Test]
        public async Task If_a_fixed_quantity_of_resource_had_been_depleted_then_reservations_cant_be_made()
        {
            var reservationService = Configuration.Current.ReservationService();

            // given a fixed quantity of some resource where the resource has been used
            var ownerToken = Any.Guid().ToString();
            var promoCode = "promo-code-" + Any.Guid();
            var reservedValue = Any.Guid().ToString();
            var userConfirmationCode = Any.Guid().ToString();
            await reservationService.Reserve(reservedValue, promoCode, reservedValue, TimeSpan.FromDays(-1));

            //act
            var result = await reservationService.ReserveAny(
                scope: promoCode,
                ownerToken: ownerToken,
                lease: TimeSpan.FromMinutes(-2), 
                confirmationToken: userConfirmationCode);

            result.Should().Be(reservedValue);
            await reservationService.Confirm(userConfirmationCode, promoCode, ownerToken);

            //assert
            result = await reservationService.ReserveAny(
                scope: promoCode,
                ownerToken: Any.FullName(),
                lease: TimeSpan.FromMinutes(2), 
                confirmationToken: Any.Guid().ToString());

            result.Should().BeNull();
        }
 
        [Test]
        public async Task Confirmation_token_cant_be_used_twice_by_different_owners_for_the_same_resource()
        {
            var reservationService = Configuration.Current.ReservationService();

            // given a fixed quantity of some resource where the resource has been used
            var word = Any.Word();
            var ownerToken = Any.Guid().ToString();
            var promoCode = "promo-code-" + word;
            var reservedValue = Any.Guid().ToString();
            var confirmationToken = Any.Guid().ToString();
            await reservationService.Reserve(reservedValue, promoCode, reservedValue, TimeSpan.FromDays(-1));

            //act
            await reservationService.ReserveAny(
                scope: promoCode,
                ownerToken: ownerToken,
                confirmationToken: confirmationToken);

            //assert
            var result = await reservationService.ReserveAny(
                scope: promoCode,
                ownerToken: Any.FullName(),
                lease: TimeSpan.FromMinutes(2), 
                confirmationToken: confirmationToken);

            result.Should().BeNull();
        }

        [Test]
        public async Task When_confirmation_token_is_used_twice_for_the_same_unconfirmed_reservation_then_ReserveAny_extends_the_lease()
        {
            var reservationService = Configuration.Current.ReservationService();

            // given a fixed quantity of some resource where the resource has been used
            //todo:(this needs to be done via an interface rather then just calling reserve multiple times)
            var word = Any.Word();
            var ownerToken = Any.Guid().ToString();
            var promoCode = "promo-code-" + word;
            var reservedValue = Any.Guid().ToString();
            var confirmationToken = Any.Guid().ToString();
            await reservationService.Reserve(reservedValue, promoCode, reservedValue, TimeSpan.FromDays(-1));
            await reservationService.Reserve(Any.Guid().ToString(), promoCode, reservedValue, TimeSpan.FromDays(-1));

            //act
            var firstAttempt = await reservationService.ReserveAny(
                scope: promoCode,
                ownerToken: ownerToken,
                confirmationToken: confirmationToken);

            //assert
            var secondAttempt = await reservationService.ReserveAny(
                scope: promoCode,
                ownerToken: ownerToken,
                lease: TimeSpan.FromMinutes(2),
                confirmationToken: confirmationToken);

            secondAttempt.Should()
                         .NotBeNull()
                         .And
                         .Be(firstAttempt);
        }

        [Test]
        public async Task When_ReserveAny_is_called_for_a_scope_that_has_no_entries_at_all_then_it_returns_false()
        {
            var reservationService = Configuration.Current.ReservationService();

            var value = await reservationService.ReserveAny(Any.CamelCaseName(), Any.CamelCaseName(), TimeSpan.FromMinutes(1));

            value.Should().BeNull();
        }

        [Test]
        public async Task When_a_command_reserves_a_unique_value_but_it_expires_then_a_subsequent_request_by_a_different_actor_succeeds()
        {
            // arrange
            var username = Any.CamelCaseName(5);
            var scope = "UserName";
            await Configuration.Current.ReservationService().Reserve(username, scope, Any.CamelCaseName(), TimeSpan.FromMinutes(30));

            // act
            VirtualClock.Current.AdvanceBy(TimeSpan.FromMinutes(32));

            var attempt = new CustomerAccount()
                .Validate(new RequestUserName
                {
                    UserName = username,
                    Principal = new Customer
                    {
                        Name = Any.CamelCaseName()
                    }
                });

            // assert
            attempt.ShouldBeValid();
            var reservation = await GetReservedValue(username, scope);
            reservation.Expiration.Should().Be(Clock.Now().AddMinutes(1));
        }

        [Test]
        public async Task Reservations_can_be_placed_for_one_of_a_fixed_quantity_of_a_resource()
        {
            var reservationService = Configuration.Current.ReservationService();

            // given a fixed quantity of some resource, e.g. promo codes:
            var ownerToken = "ownerToken-" + Any.Guid();
            var promoCode = "promo-code-" + Any.Guid();
            var reservedValue = "reservedValue-" + Any.Guid();
            var confirmationToken = "userConfirmationCode-" + Any.Guid();
            await reservationService.Reserve(reservedValue, promoCode, reservedValue, TimeSpan.FromDays(-1));

            //act
            var result = await reservationService.ReserveAny(
                scope: promoCode,
                ownerToken: ownerToken,
                lease: TimeSpan.FromMinutes(2),
                confirmationToken: confirmationToken);

            result.Should().Be(reservedValue);

            await reservationService.Confirm(confirmationToken, promoCode, ownerToken);

            // assert
            var reservation = await GetReservedValue(reservedValue, promoCode);
            reservation.Expiration.Should().NotHaveValue();
        }

        [Test]
        public async Task The_value_returned_by_the_reservation_service_can_be_used_for_confirmation()
        {
            var reservationService = Configuration.Current.ReservationService();

            // given a fixed quantity of some resource, e.g. promo codes:
            var ownerToken = "owner-token-" + Any.Email();
            var promoCode = "promo-code-" + Any.Guid();
            var reservedValue = Any.Email();
            await reservationService.Reserve(reservedValue,
                scope: promoCode,
                ownerToken: ownerToken,
                lease: TimeSpan.FromDays(-1));

            //act
            var value = await reservationService.ReserveAny(
                scope: promoCode,
                ownerToken: ownerToken,
                lease: TimeSpan.FromMinutes(2));

            value.Should().NotBeNull();

            await reservationService.Confirm(value, promoCode, ownerToken);

            // assert
            var reservation = await GetReservedValue(reservedValue, promoCode);
            reservation.Expiration.Should().NotHaveValue();
            reservation.ConfirmationToken.Should().Be(value);
        }

        [Test]
        public async Task When_the_aggregate_is_saved_then_the_reservation_is_confirmed()
        {
            // arrange
            var username = Any.Email();

            var account = new CustomerAccount();
            account.Apply(new RequestUserName
            {
                UserName = username,
                Principal = new Customer(username)
            });
            Configuration.Current.EventBus.Subscribe(new UserNameConfirmer());
            var repository = Configuration.Current.Repository<CustomerAccount>();

            // act
            await repository.Save(account);

            // assert
            var reservation = await GetReservedValue(username, "UserName");
            reservation.Expiration.Should().NotHaveValue();
        }

        protected abstract Task<ReservedValue> GetReservedValue(string value, string promoCode);
    }

#pragma warning disable 618
    public class UserNameConfirmer : IHaveConsequencesWhen<CustomerAccount.UserNameAcquired>
#pragma warning restore 618
    {
        public void HaveConsequences(CustomerAccount.UserNameAcquired @event)
        {
            Configuration.Current.ReservationService().Confirm(
                @event.UserName,
                "UserName",
                @event.UserName).Wait();
        }
    }
}