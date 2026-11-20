# Keep the published .NET single-file bundle byte-for-byte intact.
%global debug_package %{nil}
%global __os_install_post %{nil}

Name:           fast-steam-tools
Version:        %{fst_version}
Release:        1
Summary:        Fast Steam Tools desktop client for native Steam on Linux
License:        See bundled license notices
URL:            https://github.com/superhe11/FST
Source0:        fst-%{version}-linux-x86_64.tar.gz
ExclusiveArch:  x86_64

Requires:       ca-certificates, glibc, libgcc, libstdc++, libicu, openssl-libs, krb5-libs, tzdata, zlib
Requires:       python3, libX11, libICE, libSM, fontconfig, libsecret, xdg-utils, dejavu-sans-fonts
Recommends:     xorg-x11-server-Xwayland
Provides:       fst = %{version}-%{release}

%description
Desktop client for native Steam on Linux. Includes the .NET runtime.

%prep
%setup -q -n fst-linux-x64

%install
install -Dm755 app/fst "%{buildroot}%{_libexecdir}/%{name}/fst"
install -Dm644 fst.desktop "%{buildroot}%{_datadir}/applications/fst.desktop"
install -Dm644 fst.svg "%{buildroot}%{_datadir}/icons/hicolor/scalable/apps/fst.svg"
install -d "%{buildroot}%{_bindir}"
ln -s "%{_libexecdir}/%{name}/fst" "%{buildroot}%{_bindir}/fst"

%files
%license LICENSES/*.txt
%dir %{_libexecdir}/%{name}
%{_libexecdir}/%{name}/fst
%{_bindir}/fst
%{_datadir}/applications/fst.desktop
%{_datadir}/icons/hicolor/scalable/apps/fst.svg
