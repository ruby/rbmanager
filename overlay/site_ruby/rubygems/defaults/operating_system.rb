# frozen_string_literal: true

# CA trust bootstrap for the official Ruby mswin package.
#
# The vcpkg-built OpenSSL bakes an OPENSSLDIR that does not exist on end-user
# machines, so certificate verification has no trust anchors out of the box.
# Until ruby/openssl can read the Windows certificate store natively (via
# OpenSSL's winstore OSSL_STORE loader), this hook exports the Windows ROOT
# store to a cached PEM file and points SSL_CERT_FILE at it for this process
# only. Installed by the windows-installer overlay, NOT part of ruby/ruby;
# remove once the upstream support exists.
#
# The export runs through a one-shot powershell.exe child process rather
# than fiddle: fiddle is a bundled gem, so it is not requireable while
# rubygems is still bootstrapping this very file, nor under `bundle exec`
# with a Gemfile that does not list it.
#
# https://github.com/ruby/windows-installer

module RubyMswin
  module WindowsRootCerts
    CACHE_MAX_AGE = 7 * 24 * 60 * 60 # seconds

    class << self
      def apply
        return unless RUBY_PLATFORM.include?("mswin")
        return if ENV["SSL_CERT_FILE"] || ENV["SSL_CERT_DIR"]
        local = ENV["LOCALAPPDATA"]
        return if local.nil? || local.empty?

        cache = File.join(local, "ruby-mswin", "windows-root-certs.pem")
        refresh(cache) unless fresh?(cache)
        # A stale cache is still better than no trust anchors, so use
        # whatever exists even when the refresh failed.
        ENV["SSL_CERT_FILE"] = cache if File.size?(cache)
      rescue StandardError, ScriptError
        # Never break ruby startup over this.
      end

      private

      def fresh?(cache)
        stat = File.stat(cache)
        stat.size > 0 && Time.now - stat.mtime < CACHE_MAX_AGE
      rescue Errno::ENOENT
        false
      end

      def refresh(cache)
        require "fileutils"
        FileUtils.mkdir_p(File.dirname(cache))
        tmp = "#{cache}.#{Process.pid}.tmp"
        export_root_store(tmp)
        File.rename(tmp, cache) if File.size?(tmp)
      ensure
        File.unlink(tmp) if tmp && File.exist?(tmp)
      end

      # Exports the current user's ROOT system store (trust anchors only;
      # deliberately not the CA store, whose intermediates must not become
      # trusted roots) as concatenated PEM.
      def export_root_store(path)
        powershell = File.join(
          ENV["SystemRoot"] || "C:/Windows",
          "System32", "WindowsPowerShell", "v1.0", "powershell.exe"
        )
        # System.Security.Cryptography.X509Store rather than the Cert: drive:
        # the certificate provider is not reliably mounted in -NoProfile
        # child sessions, while the .NET API needs nothing preloaded.
        script = <<~POWERSHELL
          $store = New-Object System.Security.Cryptography.X509Certificates.X509Store 'Root','CurrentUser'
          $store.Open('ReadOnly')
          $lines = foreach ($c in ($store.Certificates | Sort-Object Thumbprint -Unique)) {
            '-----BEGIN CERTIFICATE-----'
            [Convert]::ToBase64String($c.RawData, 'InsertLineBreaks')
            '-----END CERTIFICATE-----'
          }
          $store.Close()
          if ($lines) { [IO.File]::WriteAllLines('#{path.gsub("'", "''")}', $lines) }
        POWERSHELL
        encoded = [script.encode(Encoding::UTF_16LE)].pack("m0")
        system(powershell, "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden",
               "-EncodedCommand", encoded,
               out: File::NULL, err: File::NULL)
      end
    end
  end
end

RubyMswin::WindowsRootCerts.apply
