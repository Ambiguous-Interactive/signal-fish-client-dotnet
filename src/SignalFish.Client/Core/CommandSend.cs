namespace SignalFish.Client.Core
{
    using System;

    /// <summary>
    /// The local verdict for one command handed to a Signal Fish client:
    /// admission is decided synchronously on the calling thread, so a
    /// refused command never touches the wire and names why. An accepted
    /// command was encoded and queued for the wire; delivery failure folds
    /// back into the session as a teardown. <c>default(CommandSend)</c> is
    /// the accepted verdict.
    /// </summary>
    public readonly struct CommandSend : IEquatable<CommandSend>
    {
        /// <summary>Gets whether the command was admitted and queued.</summary>
        public bool Accepted => _refusal == default(AdmissionError);

        /// <summary>Gets why the command was refused; meaningful only when <see cref="Accepted"/> is false.</summary>
        public AdmissionError Refusal => _refusal;

        /// <summary>Gets the accepted verdict.</summary>
        public static CommandSend Admitted => default;

        private readonly AdmissionError _refusal;

        private CommandSend(AdmissionError refusal)
        {
            _refusal = refusal;
        }

        public static bool operator ==(CommandSend left, CommandSend right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(CommandSend left, CommandSend right)
        {
            return !left.Equals(right);
        }

        public bool Equals(CommandSend other)
        {
            return _refusal == other._refusal;
        }

        public override bool Equals(object obj)
        {
            return obj is CommandSend other && Equals(other);
        }

        public override int GetHashCode()
        {
            return (int)_refusal;
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return Accepted ? "Accepted" : "Refused(" + _refusal + ")";
        }

        internal static CommandSend Refused(AdmissionError refusal)
        {
            return new CommandSend(refusal);
        }
    }
}
