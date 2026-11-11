namespace Sta.DataSync;

/// <summary>Base class of all synchronization errors. Provider-specific exceptions are kept as <see cref="Exception.InnerException"/>.</summary>
public class SyncException : Exception
{
    /// <summary>Creates the exception.</summary>
    public SyncException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public SyncException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public SyncException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary><see langword="true"/> when retrying the same operation later can succeed.</summary>
    public virtual bool IsTransient => false;
}

/// <summary>A temporary failure (network, timeout, rate limit, server error). Safe to retry with backoff.</summary>
public class SyncTransientException : SyncException
{
    /// <summary>Creates the exception.</summary>
    public SyncTransientException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public SyncTransientException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public SyncTransientException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception with a server-requested delay.</summary>
    public SyncTransientException(string message, TimeSpan? retryAfter, Exception? innerException = null)
        : base(message, innerException)
    {
        RetryAfter = retryAfter;
    }

    /// <summary>Delay requested by the server, if any.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <inheritdoc />
    public override bool IsTransient => true;
}

/// <summary>Credentials are missing, expired beyond refresh, or revoked. Not retried automatically.</summary>
public class SyncAuthenticationException : SyncException
{
    /// <summary>Creates the exception.</summary>
    public SyncAuthenticationException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public SyncAuthenticationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public SyncAuthenticationException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Data changed concurrently (failed precondition, lost manifest race, local edit during synchronization).
/// Never retried blindly: the engine re-reads the state and plans again.
/// </summary>
public class SyncConcurrencyException : SyncException
{
    /// <summary>Creates the exception.</summary>
    public SyncConcurrencyException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public SyncConcurrencyException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public SyncConcurrencyException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>An item does not exist.</summary>
public class SyncItemNotFoundException : SyncException
{
    /// <summary>Creates the exception.</summary>
    public SyncItemNotFoundException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public SyncItemNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public SyncItemNotFoundException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Content failed verification (hash or length mismatch, corrupt or unsupported data). Nothing was replaced.</summary>
public class SyncIntegrityException : SyncException
{
    /// <summary>Creates the exception.</summary>
    public SyncIntegrityException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public SyncIntegrityException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public SyncIntegrityException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A permanent destination error (e.g. quota exceeded, forbidden).</summary>
public class SyncProviderException : SyncException
{
    /// <summary>Creates the exception.</summary>
    public SyncProviderException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public SyncProviderException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public SyncProviderException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Invalid configuration (unknown scope, missing provider capability, ambiguous provider).</summary>
public class SyncConfigurationException : SyncException
{
    /// <summary>Creates the exception.</summary>
    public SyncConfigurationException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public SyncConfigurationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public SyncConfigurationException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
