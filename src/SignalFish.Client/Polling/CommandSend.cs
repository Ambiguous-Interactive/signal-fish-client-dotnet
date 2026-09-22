namespace SignalFish.Client.Polling
{
    using SignalFish.Client.Core;

    /// <summary>
    /// The local verdict for one command handed to
    /// <see cref="SignalFishPollingClient"/> send methods: admission is
    /// decided synchronously on the poll thread, so a refused command never
    /// touches the wire and names why. An accepted command was encoded and
    /// handed to the transport; delivery failure folds back into the next
    /// poll as a teardown. <c>default(CommandSend)</c> is the accepted
    /// verdict.
    /// </summary>
    public readonly struct CommandSend
    {
        /// <summary>Gets whether the command was admitted and sent.</summary>
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
