﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Cassandra;
using Microsoft.AspNetCore.Identity;

namespace AspNet.Identity.Cassandra
{
    public class UserStore : IUserStore<User>, IUserLoginStore<User>, IUserClaimStore<User>,
                                      IUserPasswordStore<User>, IUserSecurityStampStore<User>,
                                      IUserTwoFactorStore<User>, IUserLockoutStore<User>, 
                                      IUserPhoneNumberStore<User>, IUserEmailStore<User>
    {
        // A cached copy of some completed tasks
        private static readonly Task<bool> TrueTask = Task.FromResult(true);
        private static readonly Task<bool> FalseTask = Task.FromResult(false);
        private static readonly Task CompletedTask = TrueTask;

        private readonly ISession _session;
        private readonly bool _disposeOfSession;

        // Reusable prepared statements, lazy evaluated
        private readonly AsyncLazy<PreparedStatement> _createUserByUserName;
        private readonly AsyncLazy<PreparedStatement> _createUserByEmail;
        private readonly AsyncLazy<PreparedStatement> _deleteUserByUserName;
        private readonly AsyncLazy<PreparedStatement> _deleteUserByEmail; 

        private readonly AsyncLazy<PreparedStatement[]> _createUser;
        private readonly AsyncLazy<PreparedStatement[]> _updateUser;
        private readonly AsyncLazy<PreparedStatement[]> _deleteUser;

        private readonly AsyncLazy<PreparedStatement> _findById;
        private readonly AsyncLazy<PreparedStatement> _findByName;
        private readonly AsyncLazy<PreparedStatement> _findByEmail; 

        private readonly AsyncLazy<PreparedStatement[]> _addLogin;
        private readonly AsyncLazy<PreparedStatement[]> _removeLogin;
        private readonly AsyncLazy<PreparedStatement> _getLogins;
        private readonly AsyncLazy<PreparedStatement> _getLoginsByProvider;

        private readonly AsyncLazy<PreparedStatement> _getClaims;
        private readonly AsyncLazy<PreparedStatement> _addClaim;
        private readonly AsyncLazy<PreparedStatement> _removeClaim;
        
        /// <summary>
        /// Creates a new instance of CassandraUserStore that will use the provided ISession instance to talk to Cassandra.  Optionally,
        /// specify whether the ISession instance should be Disposed when this class is Disposed.
        /// </summary>
        /// <param name="session">The session for talking to the Cassandra keyspace.</param>
        /// <param name="disposeOfSession">Whether to dispose of the session instance when this object is disposed.</param>
        /// <param name="createSchema">Whether to create the schema tables if they don't exist.</param>
        public UserStore(ISession session, bool disposeOfSession = false, bool createSchema = true)
        {
            _session = session;
            _disposeOfSession = disposeOfSession;

            // Create some reusable prepared statements so we pay the cost of preparing once, then bind multiple times
            _createUserByUserName = new AsyncLazy<PreparedStatement>(() => _session.PrepareAsync(
                "INSERT INTO users_by_username (username, id, password_hash, security_stamp, two_factor_enabled, access_failed_count, " +
                "lockout_enabled, lockout_end_date, phone_number, phone_number_confirmed, email, email_confirmed) " +
                "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)"));
            _createUserByEmail = new AsyncLazy<PreparedStatement>(() => _session.PrepareAsync(
                "INSERT INTO users_by_email (email, id, username, password_hash, security_stamp, two_factor_enabled, access_failed_count, " +
                "lockout_enabled, lockout_end_date, phone_number, phone_number_confirmed, email_confirmed) " +
                "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)"));

            _deleteUserByUserName = new AsyncLazy<PreparedStatement>(() => _session.PrepareAsync("DELETE FROM users_by_username WHERE username = ?"));
            _deleteUserByEmail = new AsyncLazy<PreparedStatement>(() => _session.PrepareAsync("DELETE FROM users_by_email WHERE email = ?"));
            
            // All the statements needed by the CreateAsync method
            _createUser = new AsyncLazy<PreparedStatement[]>(() => Task.WhenAll(new []
            {
                _session.PrepareAsync("INSERT INTO users (id, username, password_hash, security_stamp, two_factor_enabled, access_failed_count, " +
                                      "lockout_enabled, lockout_end_date, phone_number, phone_number_confirmed, email, email_confirmed) " +
                                      "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)"),
                _createUserByUserName.Value,
                _createUserByEmail.Value
            }));

            // All the statements needed by the DeleteAsync method
            _deleteUser = new AsyncLazy<PreparedStatement[]>(() => Task.WhenAll(new[]
            {
                _session.PrepareAsync("DELETE FROM users WHERE id = ?"),
                _deleteUserByUserName.Value,
                _deleteUserByEmail.Value
            }));

            // All the statements needed by the UpdateAsync method
            _updateUser = new AsyncLazy<PreparedStatement[]>(() => Task.WhenAll(new []
            {
                _session.PrepareAsync("UPDATE users SET username = ?, password_hash = ?, security_stamp = ?, two_factor_enabled = ?, access_failed_count = ?, " +
                                      "lockout_enabled = ?, lockout_end_date = ?, phone_number = ?, phone_number_confirmed = ?, email = ?, email_confirmed = ? " +
                                      "WHERE id = ?"),
                _session.PrepareAsync("UPDATE users_by_username SET password_hash = ?, security_stamp = ?, two_factor_enabled = ?, access_failed_count = ?, " +
                                      "lockout_enabled = ?, lockout_end_date = ?, phone_number = ?, phone_number_confirmed = ?, email = ?, email_confirmed = ? " +
                                      "WHERE username = ?"),
                _deleteUserByUserName.Value,
                _createUserByUserName.Value,
                _session.PrepareAsync("UPDATE users_by_email SET username = ?, password_hash = ?, security_stamp = ?, two_factor_enabled = ?, access_failed_count = ?, " +
                                      "lockout_enabled = ?, lockout_end_date = ?, phone_number = ?, phone_number_confirmed = ?, email_confirmed = ? " +
                                      "WHERE email = ?"),
                _deleteUserByEmail.Value,
                _createUserByEmail.Value
            }));
            
            _findById = new AsyncLazy<PreparedStatement>(() => _session.PrepareAsync("SELECT * FROM users WHERE id = ?"));
            _findByName = new AsyncLazy<PreparedStatement>(() => _session.PrepareAsync("SELECT * FROM users_by_username WHERE username = ?"));
            _findByEmail = new AsyncLazy<PreparedStatement>(() => _session.PrepareAsync("SELECT * FROM users_by_email WHERE email = ?"));
            
            _addLogin = new AsyncLazy<PreparedStatement[]>(() => Task.WhenAll(new []
            {
                _session.PrepareAsync("INSERT INTO logins (id, login_provider, provider_key) VALUES (?, ?, ?)"),
                _session.PrepareAsync("INSERT INTO logins_by_provider (login_provider, provider_key, id) VALUES (?, ?, ?)")
            }));
            _removeLogin = new AsyncLazy<PreparedStatement[]>(() => Task.WhenAll(new []
            {
                _session.PrepareAsync("DELETE FROM logins WHERE id = ? and login_provider = ? and provider_key = ?"),
                _session.PrepareAsync("DELETE FROM logins_by_provider WHERE login_provider = ? AND provider_key = ?")
            }));
            _getLogins = new AsyncLazy<PreparedStatement>(() => _session.PrepareAsync("SELECT * FROM logins WHERE id = ?"));
            _getLoginsByProvider = new AsyncLazy<PreparedStatement>(() => _session.PrepareAsync(
                "SELECT * FROM logins_by_provider WHERE login_provider = ? AND provider_key = ?"));

            _getClaims = new AsyncLazy<PreparedStatement>(() => _session.PrepareAsync("SELECT * FROM claims WHERE id = ?"));
            _addClaim = new AsyncLazy<PreparedStatement>(() => _session.PrepareAsync(
                "INSERT INTO claims (id, type, value) VALUES (?, ?, ?)"));
            _removeClaim = new AsyncLazy<PreparedStatement>(() => _session.PrepareAsync(
                "DELETE FROM claims WHERE id = ? AND type = ? AND value = ?"));

            // Create the schema if necessary
            if (createSchema)
                SchemaCreationHelper.CreateSchemaIfNotExists(session);
        }

        /// <summary>
        /// Insert a new user.
        /// </summary>
        public async Task CreateAsync(User user)
        {
            if (user == null) throw new ArgumentNullException("user");

            // TODO:  Support uniqueness for usernames/emails at the C* level using LWT?

            PreparedStatement[] prepared = await _createUser;
            var batch = new BatchStatement();

            // INSERT INTO users ...
            batch.Add(prepared[0].Bind(user.Id, user.UserName, user.PasswordHash, user.SecurityStamp, user.IsTwoFactorEnabled, user.AccessFailedCount,
                                       user.IsLockoutEnabled, user.LockoutEndDate, user.PhoneNumber, user.IsPhoneNumberConfirmed, user.Email,
                                       user.IsEmailConfirmed));

            // Only insert into username and email tables if those have a value
            if (string.IsNullOrEmpty(user.UserName) == false)
            {
                // INSERT INTO users_by_username ...
                batch.Add(prepared[1].Bind(user.UserName, user.Id, user.PasswordHash, user.SecurityStamp, user.IsTwoFactorEnabled, user.AccessFailedCount,
                                           user.IsLockoutEnabled, user.LockoutEndDate, user.PhoneNumber, user.IsPhoneNumberConfirmed, user.Email,
                                           user.IsEmailConfirmed));
            }

            if (string.IsNullOrEmpty(user.Email) == false)
            {
                // INSERT INTO users_by_email ...
                batch.Add(prepared[2].Bind(user.Email, user.Id, user.UserName, user.PasswordHash, user.SecurityStamp, user.IsTwoFactorEnabled,
                                           user.AccessFailedCount, user.IsLockoutEnabled, user.LockoutEndDate, user.PhoneNumber,
                                           user.IsPhoneNumberConfirmed, user.IsEmailConfirmed));
            }
            
            await _session.ExecuteAsync(batch).ConfigureAwait(false);
        }

        /// <summary>
        /// Update a user.
        /// </summary>
        public async Task UpdateAsync(User user)
        {
            if (user == null) throw new ArgumentNullException("user");

            PreparedStatement[] prepared = await _updateUser;
            var batch = new BatchStatement();

            // UPDATE users ...
            batch.Add(prepared[0].Bind(user.UserName, user.PasswordHash, user.SecurityStamp, user.IsTwoFactorEnabled, user.AccessFailedCount,
                                       user.IsLockoutEnabled, user.LockoutEndDate, user.PhoneNumber, user.IsPhoneNumberConfirmed, user.Email,
                                       user.IsEmailConfirmed, user.Id));

            // See if the username changed so we can decide whether we need a different users_by_username record
            string oldUserName;
            if (user.HasUserNameChanged(out oldUserName) == false && string.IsNullOrEmpty(user.UserName) == false)
            {
                // UPDATE users_by_username ... (since username hasn't changed)
                batch.Add(prepared[1].Bind(user.PasswordHash, user.SecurityStamp, user.IsTwoFactorEnabled, user.AccessFailedCount,
                                           user.IsLockoutEnabled, user.LockoutEndDate, user.PhoneNumber, user.IsPhoneNumberConfirmed, user.Email,
                                           user.IsEmailConfirmed, user.UserName));
            }
            else
            {
                // DELETE FROM users_by_username ... (delete old record since username changed)
                if (string.IsNullOrEmpty(oldUserName) == false)
                {
                    batch.Add(prepared[2].Bind(oldUserName));
                }

                // INSERT INTO users_by_username ... (insert new record since username changed)
                if (string.IsNullOrEmpty(user.UserName) == false)
                {
                    batch.Add(prepared[3].Bind(user.UserName, user.Id, user.PasswordHash, user.SecurityStamp, user.IsTwoFactorEnabled,
                                               user.AccessFailedCount, user.IsLockoutEnabled, user.LockoutEndDate, user.PhoneNumber,
                                               user.IsPhoneNumberConfirmed, user.Email, user.IsEmailConfirmed));
                }
            }

            // See if the email changed so we can decide if we need a different users_by_email record
            string oldEmail;
            if (user.HasEmailChanged(out oldEmail) == false && string.IsNullOrEmpty(user.Email) == false)
            {
                // UPDATE users_by_email ... (since email hasn't changed)
                batch.Add(prepared[4].Bind(user.UserName, user.PasswordHash, user.SecurityStamp, user.IsTwoFactorEnabled, user.AccessFailedCount,
                                           user.IsLockoutEnabled, user.LockoutEndDate, user.PhoneNumber, user.IsPhoneNumberConfirmed,
                                           user.IsEmailConfirmed, user.Email));
            }
            else
            {
                // DELETE FROM users_by_email ... (delete old record since email changed)
                if (string.IsNullOrEmpty(oldEmail) == false)
                {
                    batch.Add(prepared[5].Bind(oldEmail));
                }
                
                // INSERT INTO users_by_email ... (insert new record since email changed)
                if (string.IsNullOrEmpty(user.Email) == false)
                {
                    batch.Add(prepared[6].Bind(user.Email, user.Id, user.UserName, user.PasswordHash, user.SecurityStamp, user.IsTwoFactorEnabled,
                                           user.AccessFailedCount, user.IsLockoutEnabled, user.LockoutEndDate, user.PhoneNumber,
                                           user.IsPhoneNumberConfirmed, user.IsEmailConfirmed));
                }
            }
            
            await _session.ExecuteAsync(batch).ConfigureAwait(false);
        }

        /// <summary>
        /// Delete a user.
        /// </summary>
        public async Task DeleteAsync(User user)
        {
            if (user == null) throw new ArgumentNullException("user");

            PreparedStatement[] prepared = await _deleteUser;
            var batch = new BatchStatement();

            // DELETE FROM users ...
            batch.Add(prepared[0].Bind(user.Id));

            // Make sure the username didn't change before deleting from users_by_username (not sure this is possible, but protect ourselves anyway)
            string userName;
            if (user.HasUserNameChanged(out userName) == false)
                userName = user.UserName;

            // DELETE FROM users_by_username ...
            if (string.IsNullOrEmpty(userName) == false)
                batch.Add(prepared[1].Bind(userName));

            // Make sure email didn't change before deleting from users_by_email (also not sure this is possible)
            string email;
            if (user.HasEmailChanged(out email) == false)
                email = user.Email;

            // DELETE FROM users_by_email ...
            if (string.IsNullOrEmpty(email) == false)
                batch.Add(prepared[2].Bind(email));
            
            await _session.ExecuteAsync(batch).ConfigureAwait(false);
        }

        /// <summary>
        /// Finds a user by id.
        /// </summary>
        public async Task<User> FindByIdAsync(Guid id)
        {
            PreparedStatement prepared = await _findById;
            BoundStatement bound = prepared.Bind(id);

            RowSet rows = await _session.ExecuteAsync(bound).ConfigureAwait(false);
            return User.FromRow(rows.SingleOrDefault());
        }

        /// <summary>
        /// Find a user by name (assumes usernames are unique).
        /// </summary>
        public async Task<User> FindByNameAsync(string userName)
        {
            if (string.IsNullOrWhiteSpace(userName)) throw new ArgumentException("userName cannot be null or empty", "userName");
            
            PreparedStatement prepared = await _findByName;
            BoundStatement bound = prepared.Bind(userName);

            RowSet rows = await _session.ExecuteAsync(bound).ConfigureAwait(false);
            return User.FromRow(rows.SingleOrDefault());
        }
        
        /// <summary>
        /// Adds a user login with the specified provider and key
        /// </summary>
        public async Task AddLoginAsync(User user, UserLoginInfo login)
        {
            if (user == null) throw new ArgumentNullException("user");
            if (login == null) throw new ArgumentNullException("login");

            PreparedStatement[] prepared = await _addLogin;
            var batch = new BatchStatement();

            // INSERT INTO logins ...
            batch.Add(prepared[0].Bind(user.Id, login.LoginProvider, login.ProviderKey));

            // INSERT INTO logins_by_provider ...
            batch.Add(prepared[1].Bind(login.LoginProvider, login.ProviderKey, user.Id));

            await _session.ExecuteAsync(batch).ConfigureAwait(false);
        }

        /// <summary>
        /// Removes the user login with the specified combination if it exists
        /// </summary>
        public async Task RemoveLoginAsync(User user, UserLoginInfo login)
        {
            if (user == null) throw new ArgumentNullException("user");
            if (login == null) throw new ArgumentNullException("login");

            PreparedStatement[] prepared = await _removeLogin;
            var batch = new BatchStatement();

            // DELETE FROM logins ...
            batch.Add(prepared[0].Bind(user.Id, login.LoginProvider, login.ProviderKey));

            // DELETE FROM logins_by_provider ...
            batch.Add(prepared[1].Bind(login.LoginProvider, login.ProviderKey));
            
            await _session.ExecuteAsync(batch).ConfigureAwait(false);
        }

        /// <summary>
        /// Returns the linked accounts for this user
        /// </summary>
        public async Task<IList<UserLoginInfo>> GetLoginsAsync(User user)
        {
            if (user == null) throw new ArgumentNullException("user");

            PreparedStatement prepared = await _getLogins;
            BoundStatement bound = prepared.Bind(user.Id);

            RowSet rows = await _session.ExecuteAsync(bound).ConfigureAwait(false);
            return rows.Select(row => new UserLoginInfo(row.GetValue<string>("login_provider"), row.GetValue<string>("provider_key"))).ToList();
        }

        /// <summary>
        /// Returns the user associated with this login
        /// </summary>
        public async Task<User> FindAsync(UserLoginInfo login)
        {
            if (login == null) throw new ArgumentNullException("login");

            PreparedStatement prepared = await _getLoginsByProvider;
            BoundStatement bound = prepared.Bind(login.LoginProvider, login.ProviderKey);

            RowSet loginRows = await _session.ExecuteAsync(bound).ConfigureAwait(false);
            Row loginResult = loginRows.FirstOrDefault();
            if (loginResult == null)
                return null;

            prepared = await _findById;
            bound = prepared.Bind(loginResult.GetValue<Guid>("id"));

            RowSet rows = await _session.ExecuteAsync(bound).ConfigureAwait(false);
            return User.FromRow(rows.SingleOrDefault());
        }

        /// <summary>
        /// Returns the claims for the user with the issuer set
        /// </summary>
        public async Task<IList<Claim>> GetClaimsAsync(User user)
        {
            if (user == null) throw new ArgumentNullException("user");

            PreparedStatement prepared = await _getClaims;
            BoundStatement bound = prepared.Bind(user.Id);

            RowSet rows = await _session.ExecuteAsync(bound).ConfigureAwait(false);
            return rows.Select(row => new Claim(row.GetValue<string>("type"), row.GetValue<string>("value"))).ToList();
        }

        /// <summary>
        /// Add a new user claim
        /// </summary>
        public async Task AddClaimAsync(User user, Claim claim)
        {
            if (user == null) throw new ArgumentNullException("user");
            if (claim == null) throw new ArgumentNullException("claim");

            PreparedStatement prepared = await _addClaim;
            BoundStatement bound = prepared.Bind(user.Id, claim.Type, claim.Value);
            await _session.ExecuteAsync(bound).ConfigureAwait(false);
        }

        /// <summary>
        /// Remove a user claim
        /// </summary>
        public async Task RemoveClaimAsync(User user, Claim claim)
        {
            if (user == null) throw new ArgumentNullException("user");
            if (claim == null) throw new ArgumentNullException("claim");

            PreparedStatement prepared = await _removeClaim;
            BoundStatement bound = prepared.Bind(user.Id, claim.Type, claim.Value);

            await _session.ExecuteAsync(bound).ConfigureAwait(false);
        }

        /// <summary>
        /// Set the user password hash
        /// </summary>
        public Task SetPasswordHashAsync(User user, string passwordHash)
        {
            if (user == null) throw new ArgumentNullException("user");
            
            // Password hash can be null when removing a password from a user
            user.PasswordHash = passwordHash;
            return CompletedTask;
        }

        /// <summary>
        /// Get the user password hash
        /// </summary>
        public Task<string> GetPasswordHashAsync(User user)
        {
            if (user == null) throw new ArgumentNullException("user");
            return Task.FromResult(user.PasswordHash);
        }

        /// <summary>
        /// Returns true if a user has a password set
        /// </summary>
        public Task<bool> HasPasswordAsync(User user)
        {
            if (user == null) throw new ArgumentNullException("user");
            return string.IsNullOrEmpty(user.PasswordHash) ? FalseTask : TrueTask;
        }

        /// <summary>
        /// Set the security stamp for the user
        /// </summary>
        public Task SetSecurityStampAsync(User user, string stamp)
        {
            if (user == null) throw new ArgumentNullException("user");
            if (stamp == null) throw new ArgumentNullException("stamp");

            user.SecurityStamp = stamp;
            return CompletedTask;
        }

        /// <summary>
        /// Get the user security stamp
        /// </summary>
        public Task<string> GetSecurityStampAsync(User user)
        {
            if (user == null) throw new ArgumentNullException("user");
            return Task.FromResult(user.SecurityStamp);
        }

        /// <summary>
        /// Sets whether two factor authentication is enabled for the user
        /// </summary>
        public Task SetTwoFactorEnabledAsync(User user, bool enabled)
        {
            if (user == null) throw new ArgumentNullException("user");

            user.IsTwoFactorEnabled = enabled;
            return CompletedTask;
        }

        /// <summary>
        /// Returns whether two factor authentication is enabled for the user
        /// </summary>
        public Task<bool> GetTwoFactorEnabledAsync(User user)
        {
            if (user == null) throw new ArgumentNullException("user");
            return Task.FromResult(user.IsTwoFactorEnabled);
        }

        /// <summary>
        /// Returns the DateTimeOffset that represents the end of a user's lockout, any time in the past should be considered
        /// not locked out.
        /// </summary>
        public Task<DateTimeOffset> GetLockoutEndDateAsync(User user)
        {
            if (user == null) throw new ArgumentNullException("user");
            return Task.FromResult(user.LockoutEndDate);
        }

        /// <summary>
        /// Locks a user out until the specified end date (set to a past date, to unlock a user)
        /// </summary>
        public Task SetLockoutEndDateAsync(User user, DateTimeOffset lockoutEnd)
        {
            if (user == null) throw new ArgumentNullException("user");

            user.LockoutEndDate = lockoutEnd;
            return CompletedTask;
        }

        /// <summary>
        /// Used to record when an attempt to access the user has failed
        /// </summary>
        public Task<int> IncrementAccessFailedCountAsync(User user)
        {
            if (user == null) throw new ArgumentNullException("user");

            // NOTE:  Since we aren't using C* counters and an increment operation, the value for the counter we loaded could be stale when we
            // increment this way and so the count could be incorrect (i.e. this increment in not atomic)
            user.AccessFailedCount++;
            return Task.FromResult(user.AccessFailedCount);
        }

        /// <summary>
        /// Used to reset the access failed count, typically after the account is successfully accessed
        /// </summary>
        public Task ResetAccessFailedCountAsync(User user)
        {
            if (user == null) throw new ArgumentNullException("user");

            // Same note as above in Increment applies here
            user.AccessFailedCount = 0;
            return CompletedTask;
        }

        /// <summary>
        /// Returns the current number of failed access attempts.  This number usually will be reset whenever the password is
        /// verified or the account is locked out.
        /// </summary>
        public Task<int> GetAccessFailedCountAsync(User user)
        {
            if (user == null) throw new ArgumentNullException("user");

            return Task.FromResult(user.AccessFailedCount);
        }

        /// <summary>
        /// Returns whether the user can be locked out.
        /// </summary>
        public Task<bool> GetLockoutEnabledAsync(User user)
        {
            if (user == null) throw new ArgumentNullException("user");

            return Task.FromResult(user.IsLockoutEnabled);
        }

        /// <summary>
        /// Sets whether the user can be locked out.
        /// </summary>
        public Task SetLockoutEnabledAsync(User user, bool enabled)
        {
            if (user == null) throw new ArgumentNullException("user");

            user.IsLockoutEnabled = enabled;
            return CompletedTask;
        }

        /// <summary>
        /// Returns the user associated with this email
        /// </summary>
        public async Task<User> FindByEmailAsync(string email)
        {
            if (string.IsNullOrWhiteSpace(email)) throw new ArgumentException("email cannot be null or empty", "email");

            PreparedStatement prepared = await _findByEmail;
            BoundStatement bound = prepared.Bind(email);

            RowSet rows = await _session.ExecuteAsync(bound).ConfigureAwait(false);
            return User.FromRow(rows.SingleOrDefault());
        }

        /// <summary>
        /// Set the user email
        /// </summary>
        public Task SetEmailAsync(User user, string email)
        {
            if (user == null) throw new ArgumentNullException("user");
            if (email == null) throw new ArgumentNullException("email");

            user.Email = email;
            return CompletedTask;
        }

        /// <summary>
        /// Get the user email
        /// </summary>
        public Task<string> GetEmailAsync(User user)
        {
            if (user == null) throw new ArgumentNullException("user");
            return Task.FromResult(user.Email);
        }

        /// <summary>
        /// Returns true if the user email is confirmed
        /// </summary>
        public Task<bool> GetEmailConfirmedAsync(User user)
        {
            if (user == null) throw new ArgumentNullException("user");
            return Task.FromResult(user.IsEmailConfirmed);
        }

        /// <summary>
        /// Sets whether the user email is confirmed
        /// </summary>
        public Task SetEmailConfirmedAsync(User user, bool confirmed)
        {
            if (user == null) throw new ArgumentNullException("user");

            user.IsEmailConfirmed = confirmed;
            return CompletedTask;
        }

        /// <summary>
        /// Set the user's phone number
        /// </summary>
        public Task SetPhoneNumberAsync(User user, string phoneNumber)
        {
            if (user == null) throw new ArgumentNullException("user");
            if (phoneNumber == null) throw new ArgumentNullException("phoneNumber");

            user.PhoneNumber = phoneNumber;
            return CompletedTask;
        }

        /// <summary>
        /// Get the user phone number
        /// </summary>
        public Task<string> GetPhoneNumberAsync(User user)
        {
            if (user == null) throw new ArgumentNullException("user");
            return Task.FromResult(user.PhoneNumber);
        }

        /// <summary>
        /// Returns true if the user phone number is confirmed
        /// </summary>
        public Task<bool> GetPhoneNumberConfirmedAsync(User user)
        {
            if (user == null) throw new ArgumentNullException("user");
            return Task.FromResult(user.IsPhoneNumberConfirmed);
        }

        /// <summary>
        /// Sets whether the user phone number is confirmed
        /// </summary>
        public Task SetPhoneNumberConfirmedAsync(User user, bool confirmed)
        {
            if (user == null) throw new ArgumentNullException("user");

            user.IsPhoneNumberConfirmed = confirmed;
            return CompletedTask;
        }
        
        protected void Dispose(bool disposing)
        {
            if (_disposeOfSession)
                _session.Dispose();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
