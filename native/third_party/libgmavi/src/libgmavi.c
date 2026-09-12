/*
*	Copyright (c) 2022 Gijs Oosterling
*	All rights reserved.
*	
*		Permission is hereby granted, free of charge, to any person obtaining a copy
*		of this software and associated documentation files (the "Software"), to deal
*		in the Software without restriction, including without limitation the rights
*		to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
*		copies of the Software, and to permit persons to whom the Software is
*		furnished to do so, subject to the following conditions:
*	
*		The above copyright notice and this permission notice shall be included in all
*		copies or substantial portions of the Software.
*	
*		THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
*		IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
*		FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
*		AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
*		LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
*		OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
*		SOFTWARE.
*	
*	Redistributions in binary form must reproduce the above copyright notice.
*/

#include <stdio.h>
#include <io.h>
#include <stdlib.h>
#include <string.h>
#include <stdbool.h>
#include "../include/libgmavi.h"
#include "aviStruct.h"
#include <errno.h>
#include <sys/stat.h>

static bool	gmav_error(gmavi_t *avi, uint32_t errorCode, const char *additionalString)
{
	if (avi)
	{
		if (avi->fileHandler != NULL)
		{
			fclose(avi->fileHandler);
			avi->fileHandler = NULL;
		}
		for (uint32_t i = 0; i < avi->riffChunks; i++) {
			if (avi->ix00[i].avixIndexEntries != NULL)
				free(avi->ix00[i].avixIndexEntries);
		}
		free(avi->filePath);
		free(avi);
	}
	if (additionalString)
	{
		printf("%s\n", additionalString);
		strerror(errorCode);
	}
	else if (errorCode == EBADF)
		strerror(errorCode);
	else if (errorCode == EACCES)
		strerror(errorCode);
	return (false);
}

void		*gmav_open(
	const char 	*filePath,
	uint32_t	width,
	uint32_t	height,
	uint32_t	framesPerSec)
{
	gmavi_fileAddr_t	fileAddr;
	gmavi_static_t		contents;
	gmavi_t				*out;

	/*	Ensure everything is zeroed	*/
	memset(&fileAddr, 0, sizeof(gmavi_fileAddr_t));
	memset(&contents, 0, sizeof(gmavi_static_t));
	out = (gmavi_t *)calloc(1, sizeof(gmavi_t));
	if (out == NULL)
	{
		gmav_error(NULL, errno, NULL);
		return (NULL);
	}
	if (filePath == NULL || width == 0 || height == 0 || framesPerSec == 0 ||
		width > (UINT32_MAX - 3u) / 3u ||
		((width * 3u + 3u) & ~3u) > UINT32_MAX / height)
	{
		gmav_error(out, EINVAL, "Invalid libgmavi path, dimensions, or frame rate");
		return (NULL);
	}

	out->filePath = _strdup(filePath);
	/* DeadLockMVM fix: BI_RGB scanlines are DWORD-aligned. */
	out->bitmapSize = ((width * 3u + 3u) & ~3u) * height;
	out->streamTickSize = out->bitmapSize + 8;
	out->maxFrames = 1999991696 / out->streamTickSize;

	contents.main = (RIFFLIST){
		FCC('RIFF'),						/*	fcc					*/
		TO_BE_DETERMINED,					/*	cb					*/
		FCC('AVI ')							/*	fccListType			*/
	};

	fileAddr._fileBaseStart = (uint64_t)&contents.main;
	fileAddr.cbMain = (uint64_t)&contents.main.cb - fileAddr._fileBaseStart;	// 0x2

	contents.hdrl = (RIFFLIST){
		FCC('LIST'),						/*	fcc					*/
		STATIC_HEADER_LIST_SIZE,			/*	cb					*/
		FCC('hdrl')							/*	fccListType			*/
	};

	/*
	 * DeadLockMVM fix: consumers derive the reported bit rate by multiplying
	 * maxBytesPerSec by 8 in a signed 32-bit field. A raw 300 FPS 1080p stream
	 * is ~1.87 GB/s, so a value above 0x0FFFFFFF overflows that product and the
	 * Windows property handler reports 0 kbps. Reference muxers cap the field
	 * at 0x0FFFFFFF for the same reason.
	 */
	uint32_t	avihMaxBytesPerSec;
	if ((uint32_t)0x0FFFFFFF / out->bitmapSize > framesPerSec)
		avihMaxBytesPerSec = out->bitmapSize * framesPerSec;
	else
		avihMaxBytesPerSec = 0x0FFFFFFF;

	contents.aviHeader = (AVIMAINHEADER){
		FCC('avih'),						/*	fcc					*/
		STATIC_AVI_HEADER_SIZE,				/*	cb					*/
		1000000 / framesPerSec,				/*	microSecPerFrame	*/
		avihMaxBytesPerSec,					/*	maxBytesPerSec		*/
		0,									/*	paddingGranularity	*/
		AVIF_HASINDEX,						/*	flags				*/
		TO_BE_DETERMINED,					/*	totalFrames			*/
		0,									/*	initialFames		*/
		1,									/*	streams				*/
		out->bitmapSize,					/*	suggestedBufferSize	*/
		width,								/*	width				*/
		height								/*	height				*/
	};

	fileAddr.firstFrames = 0x30;

	contents.strl = (RIFFLIST){
		FCC('LIST'),						/*	fcc					*/
		STATIC_STREAM_LIST_SIZE,			/*	cb					*/
		FCC('strl')							/*	fccListType			*/
	};

	contents.streamHeader = (AVISTREAMHEADER){
		FCC('strh'),						/*	fcc					*/
		STATIC_STREAM_HEADER_SIZE,			/*	cb					*/
		FCC('vids'),						/*	fccType				*/
		FCC('DIB '),						/*	fccHandler			*/
		0,									/*	flags				*/
		0,									/*	priority			*/
		0,									/*	language			*/
		0,									/*	initialFrames		*/
		1,									/*	scale				*/
		framesPerSec,						/*	rate				*/
		0,									/*	start				*/
		TO_BE_DETERMINED,					/*	length				*/
		out->bitmapSize,					/*	suggestedBufferSize	*/
		0xFFFFFFFF,							/*	quality				*/
		0,									/*	sampleSize			*/
		(RECT){0, 0, width, height}			/*	frame				*/
	};
	
	fileAddr.totalFrames = 0x8C;

	contents.strf = (RIFFCHUNK){
		FCC('strf'),						/*	fcc					*/
		STATIC_STREAM_FORMAT_SIZE			/*	cb					*/
	};

	contents.bitmapHeader = (BITMAPINFOHEADER){
		STATIC_BITMAP_HEADER_SIZE,			/*	size				*/
		width,								/*	width				*/
		height,								/*	height				*/
		1,									/*	planes				*/
		24,									/*	bitCount			*/
		0,									/*	compression			*/
		out->bitmapSize,					/*	sizeImage			*/
	};

	contents.superIndex = (AVISUPERINDEX){
		FCC('JUNK'),						/*	fcc					*/
		STATIC_SUPER_INDEX_SIZE,			/*	cb					*/
	};
	
	fileAddr.superIndex = (uint64_t)&contents.superIndex - fileAddr._fileBaseStart;
	fileAddr.entriesInUse = (uint64_t)&contents.superIndex.entriesInUse - fileAddr._fileBaseStart;
	fileAddr.superIndexEntries = (uint64_t)&contents.superIndex.index[0] - fileAddr._fileBaseStart;

	contents.odml = (RIFFLIST){
		FCC('LIST'),						/*	fcc					*/
		STATIC_EXTENDED_LIST_SIZE,			/*	cb					*/
		FCC('odml')							/*	fccListType			*/
	};

	contents.extendedHeader = (AVIEXTHEADER){
		FCC('dmlh'),						/*	fcc					*/
		STATIC_EXTENDED_HEADER_SIZE,		/*	cb					*/
		TO_BE_DETERMINED,					/*	grandFrames			*/
		0x0									/*	future				*/
	};
	fileAddr.grandFrames = (uint64_t)&contents.extendedHeader.grandFrames - fileAddr._fileBaseStart;

	contents.junk = (RIFFCHUNK){
		FCC('JUNK'),						/*	fcc					*/
		STATIC_JUNKFILL_SIZE				/*	cb					*/
	};

	contents.movi = (RIFFLIST){
		FCC('LIST'),						/*	fcc					*/
		TO_BE_DETERMINED,					/*	cb					*/
		FCC('movi')							/*	fccListType			*/
	};

	fileAddr.cbMovi = (uint64_t)&contents.movi.cb - fileAddr._fileBaseStart;
	fileAddr.moviStart = fileAddr.cbMovi + 8;

	out->riffSize = sizeof(gmavi_static_t) - 8;
	out->contents = contents;
	out->fileAddr = fileAddr;
	out->fileHandler = fopen(filePath, "wb+");
	if (out->fileHandler == NULL)
	{
		gmav_error(out, errno, NULL);
		return (NULL);
	}

	fwrite(&out->contents, sizeof(gmavi_static_t), 1, out->fileHandler);

	out->mainIndex.fcc = FCC('idx1');
	out->mainIndex.cb = 0;

	return (out);
}

static bool		gmav_finish_main(
	gmavi_t	*avi, bool finalWrite)
{
	avi->moviSize = (avi->frameCount * avi->streamTickSize) + 4;
	
	avi->mainIndex.cb = STATIC_OLD_INDEX_OFFSET * avi->frameCount;
	avi->riffSize += avi->moviSize + avi->mainIndex.cb + 4;
	avi->mainIndexEntries = (AVIOLDINDEX_ENTRY *)calloc(1, avi->mainIndex.cb);
	if (avi->mainIndexEntries == NULL)
		return (gmav_error(avi, errno, NULL));
	for (uint32_t i = 0; i < avi->frameCount; i++) {
		avi->mainIndexEntries[i] = (AVIOLDINDEX_ENTRY){
			FCC('00db'),					/*	chunkId				*/
			AVIF_HASINDEX,					/*	flags				*/
			4 + avi->streamTickSize * i,	/*	offset				*/
			avi->bitmapSize					/*	size				*/
		};
	}

	_fseeki64(avi->fileHandler, 0, SEEK_END);
	fwrite(&avi->mainIndex, sizeof(AVIOLDINDEX), 1, avi->fileHandler);
	fwrite(avi->mainIndexEntries, sizeof(AVIOLDINDEX_ENTRY), avi->frameCount, avi->fileHandler);
	free(avi->mainIndexEntries);
	
	_fseeki64(avi->fileHandler, avi->fileAddr.cbMain, SEEK_SET);
	fwrite(&avi->riffSize, sizeof(uint32_t), 1, avi->fileHandler);
	avi->fileSize = avi->riffSize;
	
	_fseeki64(avi->fileHandler, avi->fileAddr.firstFrames, SEEK_SET);
	fwrite(&avi->frameCount, sizeof(uint32_t), 1, avi->fileHandler);

	/* DeadLockMVM fix: the stream header length is distinct from avih totalFrames. */
	_fseeki64(avi->fileHandler, avi->fileAddr.totalFrames, SEEK_SET);
	fwrite(&avi->frameCount, sizeof(uint32_t), 1, avi->fileHandler);
	
	_fseeki64(avi->fileHandler, avi->fileAddr.grandFrames, SEEK_SET);
	fwrite(&avi->frameCount, sizeof(uint32_t), 1, avi->fileHandler);
	
	_fseeki64(avi->fileHandler, avi->fileAddr.cbMovi, SEEK_SET);
	fwrite(&avi->moviSize, sizeof(uint32_t), 1, avi->fileHandler);
	avi->fileAddr.moviStart = 0x2014;

	if (finalWrite)
	{
		if (fclose(avi->fileHandler))
		{
			avi->fileHandler = NULL;
			return (gmav_error(avi, errno, NULL));
		}
		avi->fileHandler = NULL;
		free(avi->filePath);
		free(avi);
	}
	return (true);
}

static bool gmav_create_index(gmavi_t *avi, size_t size)
{
	avi->ix00[avi->riffChunks].avixIndex = (AVISTDINDEX){
		FCC('ix00'),						/*	fcc					*/
		(uint32_t)(24u + size * 8u),		/*	cb					*/
		2,									/*	wLongsPerEntry		*/
		0,									/*	bIndexSubType		*/
		AVI_INDEX_OF_CHUNKS,				/*	bIndexType			*/
		(uint32_t)size,					/*	nEntriesInUse		*/
		FCC('00db'),						/*	dwChunkId			*/
		avi->fileAddr.moviStart,			/*	qwBaseOffset		*/
		0									/*	dwReserved_3		*/
	};

	avi->ix00[avi->riffChunks].avixIndexEntries = (AVISTDINDEX_ENTRY *)calloc(1, sizeof(AVISTDINDEX_ENTRY) * size);
	if (avi->ix00[avi->riffChunks].avixIndexEntries == NULL)
		return (gmav_error(avi, errno, NULL));
	
	for (uint32_t i = 0; i < size; i++) {
		avi->ix00[avi->riffChunks].avixIndexEntries[i].dwOffset = avi->streamTickSize * i;
		avi->ix00[avi->riffChunks].avixIndexEntries[i].dwSize = avi->bitmapSize;
	}
	return (true);
}

static bool	gmav_add_avix_chunk(gmavi_t *avi)
{
	if (avi->riffChunks == 0)
	{
		gmav_finish_main(avi, false);
		avi->fileSize += 8;
	}
	else
	{
		uint32_t	moviSize = 4 + (avi->streamTickSize * avi->maxFrames);
		uint32_t	riffSize = moviSize + 12;

		avi->fileSize += (avi->streamTickSize * avi->maxFrames);
		_fseeki64(avi->fileHandler, avi->fileAddr.cbMain, SEEK_SET);
		fwrite(&riffSize, sizeof(uint32_t), 1, avi->fileHandler);
		_fseeki64(avi->fileHandler, 8, SEEK_CUR);
		fwrite(&moviSize, sizeof(uint32_t), 1, avi->fileHandler);
	}

	RIFFLIST	avix = {
		FCC('RIFF'),						/*	fcc					*/
		TO_BE_DETERMINED,					/*	cb					*/
		FCC('AVIX')							/*	fccListType			*/
	};
	RIFFLIST	movi = {
		FCC('LIST'),						/*	fcc					*/
		TO_BE_DETERMINED,					/*	cb					*/
		FCC('movi')							/*	fccListType			*/
	};

	avi->fileAddr.cbMain = avi->fileSize + 4;

	_fseeki64(avi->fileHandler, 0, SEEK_END);
	fwrite(&avix, sizeof(RIFFLIST), 1, avi->fileHandler);
	fwrite(&movi, sizeof(RIFFLIST), 1, avi->fileHandler);
	avi->moviSize = 0;
	avi->fileSize += 24;

	gmav_create_index(avi, avi->maxFrames);
	avi->fileAddr.moviStart = avi->fileSize + 8;
	avi->riffChunks += 1;
	return (true);
}

bool	gmav_add(
	void *gmavi,
	uint8_t *buffer)
{
	gmavi_t	*avi = (gmavi_t *)gmavi;
	const uint32_t	fourcc_uncompressed = FCC('00db');

	if (avi == NULL)
		return (gmav_error(avi, 0, "No gmavi_t struct specified (null)"));
	if (buffer == NULL)
		return (gmav_error(avi, 0, "No buffer specified (null)"));

	if (avi->frameCount && avi->frameCount % avi->maxFrames == 0)
		gmav_add_avix_chunk(avi);
	
	avi->frameCount += 1;

	/*
	 * DeadLockMVM records sequentially and every header/index transition leaves
	 * the stream at EOF. Seeking to EOF again for every multi-megabyte frame
	 * adds a synchronous CRT/file-system operation to every enabled pass.
	 */
	if (avi->riffChunks != 0)
	{
		fwrite(&fourcc_uncompressed, sizeof(uint32_t), 1, avi->fileHandler);
		fwrite(&avi->bitmapSize, sizeof(uint32_t), 1, avi->fileHandler);
		fwrite(buffer, avi->bitmapSize, 1, avi->fileHandler);
		return (true);
	}

	fwrite(&fourcc_uncompressed, sizeof(uint32_t), 1, avi->fileHandler);
	fwrite(&avi->bitmapSize, sizeof(uint32_t), 1, avi->fileHandler);
	fwrite(buffer, avi->bitmapSize, 1, avi->fileHandler);
	return (true);
}

bool		gmav_finish(
	void *gmavi)
{
	if (gmavi == NULL)
		return (gmav_error(NULL, 0, "No gmavi_t struct specified (null)"));
	gmavi_t	*avi = (gmavi_t *)gmavi;

	if (avi->riffChunks == 0)
		return (gmav_finish_main(avi, true));

	uint32_t	framesLeft = avi->frameCount % avi->maxFrames;
	if (framesLeft == 0)	//	edge case
		framesLeft = avi->maxFrames;

	gmav_create_index(avi, framesLeft);

	_fseeki64(avi->fileHandler, avi->fileAddr.totalFrames, SEEK_SET);
	fwrite(&avi->frameCount, sizeof(uint32_t), 1, avi->fileHandler);

	AVISUPERINDEX	superIndex = {
		FCC('indx'),						/*	fcc					*/
		STATIC_SUPER_INDEX_SIZE,			/*	cb					*/
		4,									/*	longsPerEntry		*/
		0,									/*	indexSubType		*/
		0,									/*	indexType			*/
		avi->riffChunks + 1,				/*	entriesInUse		*/
		FCC('00db'),						/*	chunkId				*/
		{0, 0, 0},							/*	reserved			*/
		TO_BE_DETERMINED					/*	index[]				*/
	};
	_fseeki64(avi->fileHandler, avi->fileAddr.superIndex, SEEK_SET);
	fwrite(&superIndex, sizeof(AVISUPERINDEX), 1, avi->fileHandler);
	avi->fileSize += avi->streamTickSize * framesLeft;
	
	for (uint32_t i = 0; i < avi->riffChunks; i++) {

		_fseeki64(avi->fileHandler, 0, SEEK_END);
		fwrite(&avi->ix00[i].avixIndex, sizeof(AVISTDINDEX), 1, avi->fileHandler);
		fwrite(avi->ix00[i].avixIndexEntries, sizeof(AVISTDINDEX_ENTRY), avi->maxFrames, avi->fileHandler);
		
		uint32_t	stdIndexSize = 32 + avi->maxFrames * 8;
		_fseeki64(avi->fileHandler, avi->fileAddr.superIndexEntries, SEEK_SET);
		fwrite(&avi->fileSize, sizeof(uint64_t), 1, avi->fileHandler);
		fwrite(&stdIndexSize, sizeof(uint32_t), 1, avi->fileHandler);
		fwrite(&avi->maxFrames, sizeof(uint32_t), 1, avi->fileHandler);

		free(avi->ix00[i].avixIndexEntries);
		avi->fileSize += sizeof(AVISTDINDEX_ENTRY) * avi->maxFrames;
		avi->fileSize += sizeof(AVISTDINDEX);
		avi->fileAddr.superIndexEntries += STATIC_SUPER_INDEX_OFFSET;
	}

	uint32_t	lastIndexSize = 32 * (avi->riffChunks + 1) + framesLeft * 8;
	fwrite(&avi->fileSize, sizeof(uint64_t), 1, avi->fileHandler);
	fwrite(&lastIndexSize, sizeof(uint32_t), 1, avi->fileHandler);
	fwrite(&framesLeft, sizeof(uint32_t), 1, avi->fileHandler);

	_fseeki64(avi->fileHandler, 0, SEEK_END);
	fwrite(&avi->ix00[avi->riffChunks].avixIndex, sizeof(AVISTDINDEX), 1, avi->fileHandler);
	fwrite(avi->ix00[avi->riffChunks].avixIndexEntries, sizeof(AVISTDINDEX_ENTRY), framesLeft, avi->fileHandler);

	uint32_t	riffSize = (avi->streamTickSize * framesLeft) + 16 + sizeof(AVISTDINDEX) * (avi->riffChunks + 1);
	riffSize += sizeof(AVISTDINDEX_ENTRY) * avi->maxFrames * avi->riffChunks;
	riffSize += sizeof(AVISTDINDEX_ENTRY) * framesLeft;
	uint32_t	moviSize = riffSize - 12;

	_fseeki64(avi->fileHandler, avi->fileAddr.cbMain, SEEK_SET);
	fwrite(&riffSize, sizeof(uint32_t), 1, avi->fileHandler);
	_fseeki64(avi->fileHandler, 8, SEEK_CUR);
	fwrite(&moviSize, sizeof(uint32_t), 1, avi->fileHandler);
	if (fclose(avi->fileHandler))
	{
		avi->fileHandler = NULL;
		return (gmav_error(avi, errno, NULL));
	}
	avi->fileHandler = NULL;
	free(avi->ix00[avi->riffChunks].avixIndexEntries);
	free(avi->filePath);
	free(avi);
	return (true);
}
