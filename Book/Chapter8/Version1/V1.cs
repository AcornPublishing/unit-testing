﻿using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Dynamic;
using System.Linq;
using Dapper;
using FluentAssertions;
using Moq;
using Xunit;

namespace Book.Chapter8.Version1
{
    public class User
    {
        public int UserId { get; set; }
        public string Email { get; private set; }
        public UserType Type { get; private set; }
        public bool IsEmailConfirmed { get; private set; }
        public List<EmailChangedEvent> EmailChangedEvents { get; private set; }

        public User(int userId, string email, UserType type, bool isEmailConfirmed)
        {
            UserId = userId;
            Email = email;
            Type = type;
            IsEmailConfirmed = isEmailConfirmed;
            EmailChangedEvents = new List<EmailChangedEvent>();
        }

        public string CanChangeEmail()
        {
            if (IsEmailConfirmed)
                return "Can't change email after it's confirmed";

            return null;
        }

        public void ChangeEmail(string newEmail, Company company)
        {
            Precondition.Requires(CanChangeEmail() == null);

            if (Email == newEmail)
                return;

            UserType newType = company.IsEmailCorporate(newEmail)
                ? UserType.Employee
                : UserType.Customer;

            if (Type != newType)
            {
                int delta = newType == UserType.Employee ? 1 : -1;
                company.ChangeNumberOfEmployees(delta);
            }

            Email = newEmail;
            Type = newType;
            EmailChangedEvents.Add(new EmailChangedEvent(UserId, newEmail));
        }
    }

    public class UserController
    {
        private readonly Database _database;
        private readonly IMessageBus _messageBus;

        public UserController(Database database, IMessageBus messageBus)
        {
            _database = database;
            _messageBus = messageBus;
        }

        public string ChangeEmail(int userId, string newEmail)
        {
            object[] userData = _database.GetUserById(userId);
            User user = UserFactory.Create(userData);

            string error = user.CanChangeEmail();
            if (error != null)
                return error;

            object[] companyData = _database.GetCompany();
            Company company = CompanyFactory.Create(companyData);

            user.ChangeEmail(newEmail, company);

            _database.SaveCompany(company);
            _database.SaveUser(user);
            foreach (EmailChangedEvent ev in user.EmailChangedEvents)
            {
                _messageBus.SendEmailChangedMessage(ev.UserId, ev.NewEmail);
            }

            return "OK";
        }
    }

    public class IntegrationTests
    {
        private const string ConnectionString = @"Server=.\Sql;Database=IntegrationTests;Trusted_Connection=true;";

        [Fact]
        public void Changing_email_from_corporate_to_non_corporate()
        {
            // Arrange
            var db = new Database(ConnectionString);
            User user = CreateUser(
                "user@mycorp.com", UserType.Employee, db);
            CreateCompany("mycorp.com", 1, db);

            var messageBusMock = new Mock<IMessageBus>();
            var sut = new UserController(db, messageBusMock.Object);

            // Act
            string result = sut.ChangeEmail(user.UserId, "new@gmail.com");

            // Assert
            Assert.Equal("OK", result);

            object[] userData = db.GetUserById(user.UserId);
            User userFromDb = UserFactory.Create(userData);
            Assert.Equal("new@gmail.com", userFromDb.Email);
            Assert.Equal(UserType.Customer, userFromDb.Type);

            object[] companyData = db.GetCompany();
            Company companyFromDb = CompanyFactory.Create(companyData);
            Assert.Equal(0, companyFromDb.NumberOfEmployees);

            messageBusMock.Verify(
                x => x.SendEmailChangedMessage(user.UserId, "new@gmail.com"),
                Times.Once);
        }

        private Company CreateCompany(string domainName, int numberOfEmployees, Database database)
        {
            var company = new Company(domainName, numberOfEmployees);
            database.SaveCompany(company);
            return company;
        }

        private User CreateUser(string email, UserType type, Database database)
        {
            var user = new User(0, email, type, false);
            database.SaveUser(user);
            return user;
        }
    }

    public class Database
    {
        private readonly string _connectionString;

        public Database(string connectionString)
        {
            _connectionString = connectionString;
        }

        public object[] GetUserById(int userId)
        {
            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                string query = "SELECT * FROM [dbo].[User] WHERE UserID = @UserID";
                dynamic data = connection.QuerySingle(query, new { UserID = userId });

                return new object[]
                {
                    data.UserID,
                    data.Email,
                    data.Type,
                    data.IsEmailConfirmed
                };
            }
        }

        public void SaveUser(User user)
        {
            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                string updateQuery = @"
                    UPDATE [dbo].[User]
                    SET Email = @Email, Type = @Type, IsEmailConfirmed = @IsEmailConfirmed
                    WHERE UserID = @UserID
                    SELECT @UserID";

                string insertQuery = @"
                    INSERT [dbo].[User] (Email, Type, IsEmailConfirmed)
                    VALUES (@Email, @Type, @IsEmailConfirmed)
                    SELECT CAST(SCOPE_IDENTITY() as int)";

                string query = user.UserId == 0 ? insertQuery : updateQuery;
                int userId = connection.Query<int>(query, new
                    {
                        user.Email,
                        user.UserId,
                        user.IsEmailConfirmed,
                        Type = (int)user.Type
                    })
                    .Single();

                user.UserId = userId;
            }
        }

        public object[] GetCompany()
        {
            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                string query = "SELECT * FROM dbo.Company";
                dynamic data = connection.QuerySingle(query);

                return new object[]
                {
                    data.DomainName,
                    data.NumberOfEmployees
                };
            }
        }

        public void SaveCompany(Company company)
        {
            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                string query = @"
                    UPDATE dbo.Company
                    SET DomainName = @DomainName, NumberOfEmployees = @NumberOfEmployees";

                connection.Execute(query, new
                {
                    company.DomainName,
                    company.NumberOfEmployees
                });
            }
        }
    }

    public class EmailChangedEvent
    {
        public int UserId { get; }
        public string NewEmail { get; }

        public EmailChangedEvent(int userId, string newEmail)
        {
            UserId = userId;
            NewEmail = newEmail;
        }

        protected bool Equals(EmailChangedEvent other)
        {
            return UserId == other.UserId && string.Equals(NewEmail, other.NewEmail);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj))
            {
                return false;
            }

            if (ReferenceEquals(this, obj))
            {
                return true;
            }

            if (obj.GetType() != this.GetType())
            {
                return false;
            }

            return Equals((EmailChangedEvent)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (UserId * 397) ^ (NewEmail != null ? NewEmail.GetHashCode() : 0);
            }
        }
    }

    public class UserFactory
    {
        public static User Create(object[] data)
        {
            Precondition.Requires(data.Length >= 3);

            int id = (int)data[0];
            string email = (string)data[1];
            UserType type = (UserType)data[2];
            bool isEmailConfirmed = (bool)data[3];

            return new User(id, email, type, isEmailConfirmed);
        }
    }

    public class Company
    {
        public string DomainName { get; private set; }
        public int NumberOfEmployees { get; private set; }

        public Company(string domainName, int numberOfEmployees)
        {
            DomainName = domainName;
            NumberOfEmployees = numberOfEmployees;
        }

        public void ChangeNumberOfEmployees(int delta)
        {
            Precondition.Requires(NumberOfEmployees + delta >= 0);

            NumberOfEmployees += delta;
        }

        public bool IsEmailCorporate(string email)
        {
            string emailDomain = email.Split('@')[1];
            return emailDomain == DomainName;
        }
    }

    public class CompanyFactory
    {
        public static Company Create(object[] data)
        {
            Precondition.Requires(data.Length >= 2);

            string domainName = (string)data[0];
            int numberOfEmployees = (int)data[1];

            return new Company(domainName, numberOfEmployees);
        }
    }

    public enum UserType
    {
        Customer = 1,
        Employee = 2
    }

    public static class Precondition
    {
        public static void Requires(bool precondition, string message = null)
        {
            if (precondition == false)
                throw new Exception(message);
        }
    }

    public interface IMessageBus
    {
        void SendEmailChangedMessage(int userId, string newEmail);
    }

    public class MessageBus : IMessageBus
    {
        private IBus _bus;

        public void SendEmailChangedMessage(int userId, string newEmail)
        {
            _bus.Send($"Subject: USER; Type: EMAIL CHANGED; Id: {userId}; NewEmail: {newEmail}");
        }
    }

    internal interface IBus
    {
        void Send(string message);
    }
}
