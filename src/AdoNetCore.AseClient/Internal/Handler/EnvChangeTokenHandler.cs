using System;
using System.Collections.Generic;
using System.Text;
using AdoNetCore.AseClient.Enum;
using AdoNetCore.AseClient.Interface;
using AdoNetCore.AseClient.Token;

namespace AdoNetCore.AseClient.Internal.Handler
{
    internal class EnvChangeTokenHandler : ITokenHandler
    {
        //not entirely sure what charsets ASE supports, there are quite a few mentioned in:
        //  * Selecting the Character Set for Your Server - (http://infocenter.sybase.com/help/index.jsp?topic=/com.sybase.infocenter.dc31654.1600/doc/html/san1360629216676.html)
        //  * https://www.connectionstrings.com/ase-unsupported-charset/
        //  * master.dbo.syscharsets
        //the ones of interest seem to be: iso_1 (or ???), ascii_8, utf-8 (or utf8???), 
        private static readonly Dictionary<string, Func<Encoding>> CharsetMap = new Dictionary<string, Func<Encoding>>(StringComparer.OrdinalIgnoreCase)
        {
            {"iso_1", () => Encoding.GetEncoding("ISO-8859-1")},
            {"iso 8859-1", () => Encoding.GetEncoding("ISO-8859-1")},
            {"iso88591", () => Encoding.GetEncoding("ISO-8859-1")},
            {"ascii_8", () => Encoding.ASCII},
            {"utf-8", () => Encoding.UTF8},
            {"utf8", () => Encoding.UTF8},
        };
        private readonly DbEnvironment _environment;
        private readonly string _clientRequestedCharset;
        private readonly string _actualCharset;

        /// <param name="environment">Where the resolved <see cref="Encoding"/> gets stored once known.</param>
        /// <param name="clientRequestedCharset">
        ///     The <c>Charset</c> connection string keyword — only used as a fallback if the server's
        ///     ENVCHANGE response doesn't specify one (see <see cref="GetNewCharset"/>). The server's own
        ///     declared charset otherwise always wins.
        /// </param>
        /// <param name="actualCharset">
        ///     The <c>ActualCharset</c> connection string keyword — when set, unconditionally overrides
        ///     whatever charset the server declares (see <see cref="ApplyNewEncoding"/>). Exists for
        ///     servers that declare one charset (commonly a stale/default install setting, e.g. `cp850`)
        ///     while the bytes actually on disk were written in a different one (commonly `windows-1252`,
        ///     from a client that never negotiated charset correctly) — a mismatch that otherwise produces
        ///     silently wrong (but not erroring) text for every string column. Verified against a real
        ///     production case: byte `0xED` is `í` in `windows-1252` but `Ý` in `cp850`. Applies to both
        ///     reads and writes, since the server doesn't perform any real charset conversion in this
        ///     scenario either way (bytes pass through as sent) — see `Chiola.EntityFrameworkCore.Ase`'s
        ///     `DECISIONS.md` for the read-only workaround this superseded, and this project's own
        ///     `DECISIONS.md` for why this is the better fix location.
        /// </param>
        public EnvChangeTokenHandler(DbEnvironment environment, string clientRequestedCharset, string actualCharset = null)
        {
            _environment = environment;
            _clientRequestedCharset = clientRequestedCharset;
            _actualCharset = actualCharset;
        }

        public bool CanHandle(TokenType type)
        {
            return type == TokenType.TDS_ENVCHANGE || type == TokenType.TDS_OPTIONCMD;
        }

        public void Handle(IToken token)
        {
            switch (token)
            {
                case EnvironmentChangeToken t:
                    foreach (var change in t.Changes)
                    {
                        Logger.Instance?.WriteLine($"{t.Type}: {change.Type} - {change.OldValue} -> {change.NewValue}");
                        switch (change.Type)
                        {
                            case EnvironmentChangeToken.ChangeType.TDS_ENV_DB:
                                _environment.Database = change.NewValue;
                                break;
                            case EnvironmentChangeToken.ChangeType.TDS_ENV_PACKSIZE:
                                if (int.TryParse(change.NewValue, out int newPackSize))
                                {
                                    _environment.PacketSize = newPackSize;
                                }
                                break;
                            case EnvironmentChangeToken.ChangeType.TDS_ENV_CHARSET:
                                ApplyNewEncoding(GetNewCharset(change.NewValue));
                                break;
                        }
                    }
                    break;
                case OptionCommandToken o:
                    if (o.Option == OptionCommandToken.OptionType.TDS_OPT_TEXTSIZE)
                    {
                        _environment.TextSize = BitConverter.ToInt32(o.Arguments, 0);
                        Logger.Instance?.WriteLine($"{o.Type}: {o.Option} -> {_environment.TextSize}");
                    }
                    break;
                default:
                    return;
            }
        }

        // If the change token does not specify the new charset
        // then use the client requested charset instead
        private string GetNewCharset(string newValue)
        {
            string newCharset = newValue ?? string.Empty;
            if (newCharset.Equals(string.Empty))
            {
                newCharset = _clientRequestedCharset ?? string.Empty;
            }
            return newCharset;
        }

        private void ApplyNewEncoding(string newCharset)
        {
            // ActualCharset always wins, regardless of what the server declared - see the constructor's
            // XML doc for why (a mismatched/stale server-declared charset should not silently corrupt
            // every string column just because the server thinks it knows better).
            if (!string.IsNullOrEmpty(_actualCharset))
            {
                try
                {
                    _environment.Encoding = Encoding.GetEncoding(_actualCharset);
                }
                catch
                {
                    throw new AseException($"ActualCharset '{_actualCharset}' is not a supported charset. To add support for this charset, register an EncodingProvider to handle targeting '{_actualCharset}'.");
                }

                return;
            }

            if (!newCharset.Equals(string.Empty))
            {
                if (CharsetMap.ContainsKey(newCharset))
                {
                    _environment.Encoding = CharsetMap[newCharset]();
                }
                else
                {
                    try
                    {
                        // save it for later
                        var newEncoding = Encoding.GetEncoding(newCharset);
                        CharsetMap[newCharset] = () => newEncoding;

                        _environment.Encoding = newEncoding;
                    }
                    catch
                    {
                        throw new AseException($"Server environment changed to unsupported charset '{newCharset}'. To add support for this charset, register an EncodingProvider to handle targeting '{newCharset}'.");
                    }
                }
            }
        }
    }
}
