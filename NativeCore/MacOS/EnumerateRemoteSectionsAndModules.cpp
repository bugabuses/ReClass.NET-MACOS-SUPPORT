#include <mach/mach.h>
#include <mach/mach_vm.h>
#include <mach-o/dyld_images.h>
#include <mach-o/loader.h>
#include <cstring>
#include <string>
#include <vector>
#include <algorithm>

#include "TaskPorts.hpp"

namespace
{
	struct Segment
	{
		uint64_t Start;
		uint64_t End;
		char Name[16];
	};

	struct Module
	{
		uint64_t Base = 0;
		uint64_t Size = 0;
		std::string Path;
		std::vector<Segment> Segments;
	};

	bool ReadRemote(mach_port_t task, uint64_t address, void* out, size_t size)
	{
		mach_vm_size_t outSize = 0;
		const auto kr = mach_vm_read_overwrite(task, address, size, reinterpret_cast<mach_vm_address_t>(out), &outSize);
		return kr == KERN_SUCCESS && outSize == size;
	}

	std::string ReadRemoteString(mach_port_t task, uint64_t address, size_t maxLength)
	{
		std::string result;
		char chunk[64];
		while (result.size() < maxLength)
		{
			if (!ReadRemote(task, address + result.size(), chunk, sizeof(chunk)))
			{
				// Fall back to byte-wise read near page boundaries.
				char c = 0;
				if (!ReadRemote(task, address + result.size(), &c, 1) || c == 0) break;
				result.push_back(c);
				continue;
			}
			const auto nul = static_cast<const char*>(std::memchr(chunk, 0, sizeof(chunk)));
			if (nul != nullptr)
			{
				result.append(chunk, nul - chunk);
				break;
			}
			result.append(chunk, sizeof(chunk));
		}
		return result;
	}

	// Parses a Mach-O 64 header at base, filling segments and total size.
	bool ParseImage(mach_port_t task, uint64_t base, Module& module)
	{
		mach_header_64 header = {};
		if (!ReadRemote(task, base, &header, sizeof(header)) || header.magic != MH_MAGIC_64 || header.ncmds > 4096)
		{
			return false;
		}

		std::vector<uint8_t> commands(header.sizeofcmds);
		if (header.sizeofcmds == 0 || header.sizeofcmds > 1024 * 1024 || !ReadRemote(task, base + sizeof(header), commands.data(), commands.size()))
		{
			return false;
		}

		// Slide = actual base - preferred __TEXT vmaddr.
		uint64_t textVmAddr = 0;
		bool haveText = false;
		size_t offset = 0;
		for (uint32_t i = 0; i < header.ncmds && offset + sizeof(load_command) <= commands.size(); ++i)
		{
			const auto* lc = reinterpret_cast<const load_command*>(commands.data() + offset);
			if (lc->cmdsize < sizeof(load_command) || offset + lc->cmdsize > commands.size()) break;
			if (lc->cmd == LC_SEGMENT_64)
			{
				const auto* seg = reinterpret_cast<const segment_command_64*>(lc);
				if (std::strncmp(seg->segname, "__TEXT", 16) == 0) { textVmAddr = seg->vmaddr; haveText = true; }
			}
			offset += lc->cmdsize;
		}
		if (!haveText) return false;
		const uint64_t slide = base - textVmAddr;

		uint64_t lowest = UINT64_MAX, highest = 0;
		offset = 0;
		for (uint32_t i = 0; i < header.ncmds && offset + sizeof(load_command) <= commands.size(); ++i)
		{
			const auto* lc = reinterpret_cast<const load_command*>(commands.data() + offset);
			if (lc->cmdsize < sizeof(load_command) || offset + lc->cmdsize > commands.size()) break;
			if (lc->cmd == LC_SEGMENT_64)
			{
				const auto* seg = reinterpret_cast<const segment_command_64*>(lc);
				if (std::strncmp(seg->segname, "__PAGEZERO", 16) != 0 && seg->vmsize > 0)
				{
					Segment s = {};
					s.Start = seg->vmaddr + slide;
					s.End = s.Start + seg->vmsize;
					std::strncpy(s.Name, seg->segname, 16);
					module.Segments.push_back(s);
					lowest = std::min(lowest, s.Start);
					highest = std::max(highest, s.End);
				}
			}
			offset += lc->cmdsize;
		}

		if (module.Segments.empty()) return false;
		module.Base = base;
		module.Size = highest - base;
		return true;
	}

	std::vector<Module> EnumerateModules(mach_port_t task)
	{
		std::vector<Module> modules;

		task_dyld_info_data_t dyldInfo = {};
		mach_msg_type_number_t count = TASK_DYLD_INFO_COUNT;
		if (task_info(task, TASK_DYLD_INFO, reinterpret_cast<task_info_t>(&dyldInfo), &count) != KERN_SUCCESS || dyldInfo.all_image_info_addr == 0)
		{
			return modules;
		}

		dyld_all_image_infos infos = {};
		// Only the leading fields are needed; read the whole struct size defensively.
		if (!ReadRemote(task, dyldInfo.all_image_info_addr, &infos, std::min<size_t>(sizeof(infos), dyldInfo.all_image_info_size)))
		{
			return modules;
		}
		if (infos.infoArrayCount == 0 || infos.infoArrayCount > 8192 || infos.infoArray == nullptr)
		{
			return modules;
		}

		std::vector<dyld_image_info> images(infos.infoArrayCount);
		if (!ReadRemote(task, reinterpret_cast<uint64_t>(infos.infoArray), images.data(), images.size() * sizeof(dyld_image_info)))
		{
			return modules;
		}

		for (const auto& image : images)
		{
			Module m;
			if (!ParseImage(task, reinterpret_cast<uint64_t>(image.imageLoadAddress), m)) continue;
			m.Path = ReadRemoteString(task, reinterpret_cast<uint64_t>(image.imageFilePath), PATH_MAXIMUM_LENGTH - 1);
			modules.push_back(std::move(m));
		}

		// dyld itself is not in the info array.
		if (infos.dyldImageLoadAddress != nullptr)
		{
			Module m;
			if (ParseImage(task, reinterpret_cast<uint64_t>(infos.dyldImageLoadAddress), m))
			{
				m.Path = infos.dyldPath != nullptr ? ReadRemoteString(task, reinterpret_cast<uint64_t>(infos.dyldPath), PATH_MAXIMUM_LENGTH - 1) : "/usr/lib/dyld";
				modules.push_back(std::move(m));
			}
		}

		return modules;
	}

	SectionProtection ToProtection(vm_prot_t p)
	{
		auto result = SectionProtection::NoAccess;
		if (p & VM_PROT_READ) result |= SectionProtection::Read;
		if (p & VM_PROT_WRITE) result |= SectionProtection::Write;
		if (p & VM_PROT_EXECUTE) result |= SectionProtection::Execute;
		return result;
	}

	const Module* FindModule(const std::vector<Module>& modules, uint64_t address, const Segment** segmentOut)
	{
		for (const auto& m : modules)
		{
			for (const auto& s : m.Segments)
			{
				if (address >= s.Start && address < s.End)
				{
					*segmentOut = &s;
					return &m;
				}
			}
		}
		*segmentOut = nullptr;
		return nullptr;
	}
}

extern "C" void RC_CallConv EnumerateRemoteSectionsAndModules(RC_Pointer handle, EnumerateRemoteSectionsCallback callbackSection, EnumerateRemoteModulesCallback callbackModule)
{
	if (callbackSection == nullptr && callbackModule == nullptr)
	{
		return;
	}

	const auto task = TaskPorts::Get(HandleToPid(handle));
	if (task == MACH_PORT_NULL)
	{
		return;
	}

	const auto modules = EnumerateModules(task);

	if (callbackSection != nullptr)
	{
		mach_vm_address_t address = 0;
		natural_t depth = 0;
		while (true)
		{
			mach_vm_size_t size = 0;
			vm_region_submap_info_data_64_t info = {};
			mach_msg_type_number_t count = VM_REGION_SUBMAP_INFO_COUNT_64;
			const auto kr = mach_vm_region_recurse(task, &address, &size, &depth, reinterpret_cast<vm_region_recurse_info_t>(&info), &count);
			if (kr != KERN_SUCCESS)
			{
				break;
			}
			if (info.is_submap)
			{
				++depth;
				continue;
			}

			EnumerateRemoteSectionData section = {};
			section.BaseAddress = reinterpret_cast<RC_Pointer>(address);
			section.Size = static_cast<RC_Size>(size);
			section.Protection = ToProtection(info.protection);
			section.Type = SectionType::Unknown;
			section.Category = SectionCategory::Unknown;

			const Segment* segment = nullptr;
			const auto* module = FindModule(modules, address, &segment);
			if (module != nullptr)
			{
				section.Type = SectionType::Image;
				MultiByteToUnicode(module->Path.c_str(), section.ModulePath, PATH_MAXIMUM_LENGTH);
				MultiByteToUnicode(segment->Name, section.Name, 15);
				const bool r = info.protection & VM_PROT_READ;
				const bool w = info.protection & VM_PROT_WRITE;
				const bool x = info.protection & VM_PROT_EXECUTE;
				if (r && x) section.Category = SectionCategory::CODE;
				else if (r && w) section.Category = SectionCategory::DATA;
			}
			else
			{
				section.Type = info.share_mode == SM_PRIVATE ? SectionType::Private : SectionType::Mapped;
				if (info.protection & (VM_PROT_READ | VM_PROT_WRITE))
				{
					section.Category = SectionCategory::HEAP;
				}
			}

			callbackSection(&section);

			address += size;
		}
	}

	if (callbackModule != nullptr)
	{
		for (const auto& m : modules)
		{
			EnumerateRemoteModuleData data = {};
			data.BaseAddress = reinterpret_cast<RC_Pointer>(m.Base);
			data.Size = static_cast<RC_Size>(m.Size);
			MultiByteToUnicode(m.Path.c_str(), data.Path, PATH_MAXIMUM_LENGTH);
			callbackModule(&data);
		}
	}
}
